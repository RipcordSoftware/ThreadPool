/**
 * The MIT License (MIT)
 *
 * Copyright (c) 2014-2017 Ripcord Software Ltd
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
**/

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace RipcordSoftware.ThreadPool
{
    /// <summary>
    /// The interface for simple thread pool access
    /// </summary>
    public interface IThreadPool
    {
        bool QueueUserWorkItem(WaitCallback callback, object state = null);

        int MaxThreads { get; }
        int AvailableThreads { get; }
    }

    /// <summary>
    /// Exposes access to the .NET/Mono thread pool via IThreadPool
    /// </summary>
    public class DefaultThreadPool : IThreadPool
    {
        public bool QueueUserWorkItem(WaitCallback callback, object state = null)
        {
            return System.Threading.ThreadPool.QueueUserWorkItem(callback, state);
        }

        public int MaxThreads
        {
            get
            {
                int maxWorkers = 0, maxIO = 0;
                System.Threading.ThreadPool.GetMaxThreads(out maxWorkers, out maxIO);
                return maxWorkers;
            }
        }

        public int AvailableThreads
        {
            get
            {
                int availableWorkers = 0, availableIO = 0;
                System.Threading.ThreadPool.GetAvailableThreads(out availableWorkers, out availableIO);
                return availableWorkers;
            }
        }
    }

    /// <summary>
    /// Implements an application thread pool
    /// </summary>
    public class ApplicationThreadPool : IDisposable, IThreadPool
    {
        #region Private fields
        /// <summary>
        /// The maximum number of threads to create
        /// </summary>
        private readonly int _maxThreads;

        /// <summary>
        /// The maximum number of tasks to queue
        /// </summary>
        private readonly int _maxQueueLength;

        /// <summary>
        /// A count of the number of active threads (where active means executing a callback)
        /// </summary>
        private int _activeThreads = 0;

        /// <summary>
        /// A count of the number of queued items, both pending and active
        /// </summary>
        private int _queuedItems = 0;

        /// <summary>
        /// The pool of threads
        /// </summary>
        private readonly Thread[] _threads;

        /// <summary>
        /// A semaphore controlling access to the state queue
        /// </summary>
        private readonly Semaphore _threadQueueGate;

        /// <summary>
        /// A queue of state items which contain the definition of pieces of work
        /// </summary>
        private readonly ConcurrentQueue<IThreadPoolState> _threadStateQueue = new ConcurrentQueue<IThreadPoolState>();

        /// <summary>
        /// An event used to request the threads to terminate
        /// </summary>
        private readonly EventWaitHandle _threadEnd;

        /// <summary>
        /// Count the number of exceptions thrown by worker threads
        /// </summary>
        private int _threadExceptions = 0;
        #endregion

        #region Types
        public interface IThreadPoolState
        {
            WaitCallback Callback { get; }
            object State { get; }
        }

        private class ThreadPoolState : IThreadPoolState
        {
            public ThreadPoolState(WaitCallback callback, object state)
            {
                Callback = callback;
                State = state;
            }

            public WaitCallback Callback { get; protected set; }
            public object State { get; protected set; }
        }

        public class TaskState : IThreadPoolState, IDisposable
        {
            ManualResetEvent _finished = new ManualResetEvent(false);
            volatile bool _isFinished = false;

            public TaskState(WaitCallback callback, object state)
            {
                Callback = callback;
                State = state;
            }

            public bool Join(int timeout = -1)
            {
                return _finished.WaitOne(timeout);
            }

            public static bool WaitAll(TaskState[] tasks)
            {
                foreach (var task in tasks)
                {
                    task.Join();
                }

                return true;
            }

            public void Dispose()
            {
                _finished.Dispose();
            }

            public WaitCallback Callback { get; protected set; }

            public object State { get; protected set; }

            public bool IsFinished
            {
                get { return _isFinished; }
                internal set {
                    _finished.Set();
                    _isFinished = true;
                }
            }
        }
        #endregion

        #region Constructor
        public ApplicationThreadPool(string name, int maxThreads, int maxQueueLength, bool background, ThreadPriority priority = ThreadPriority.Normal)
        {
            this._maxThreads = maxThreads;
            this._maxQueueLength = maxQueueLength;

            _threads = new Thread[maxThreads];

            // the semaphore controls access to the state queue, we start with an empty queue since nothing is available yet
            _threadQueueGate = new Semaphore(0, maxThreads);

            // an event we use to tell the threads to exit gracefully
            _threadEnd = new EventWaitHandle(false, EventResetMode.ManualReset);

            // make and start the threads
            for (int i = 0; i < maxThreads; i++)
            {
                var thread = new System.Threading.Thread(Callback);
                thread.IsBackground = background;
                thread.Priority = priority;
                thread.Name = name + "-ApplicationTheadPool-" + i.ToString();
                thread.Start();

                _threads[i] = thread;
            }
        }
        #endregion

        #region Public methods
        public TaskState QueueUserTask(WaitCallback callback, object state = null)
        {
            TaskState taskState = null;

            if (_threadStateQueue.Count < _maxQueueLength)
            {
                // increment the number of queued items
                Interlocked.Increment(ref _queuedItems);

                taskState = new TaskState(callback, state);

                // add the state item to the queue
                _threadStateQueue.Enqueue(taskState);

                // notify any waiting threads that we have a new queue entry
                NotifyWaitingQueueEntry();
            }

            return taskState;
        }

        public bool QueueUserWorkItem(WaitCallback callback, object state = null)
        {
            bool queued = false;

            if (_threadStateQueue.Count < _maxQueueLength)
            {
                // increment the number of queued items
                Interlocked.Increment(ref _queuedItems);

                // add the state item to the queue
                _threadStateQueue.Enqueue(new ThreadPoolState(callback, state));

                // notify any waiting threads that we have a new queue entry
                NotifyWaitingQueueEntry();

                queued = true;
            }

            return queued;
        }

        /// <summary>
        /// Calculates a number of threads based on a percentage size value and the number of CPU cores
        /// </summary>
        /// <returns>The thread count, will always be at least 1</returns>
        /// <param name="pcSize">A percentage size value, can exceed 100% if required</param>
        public static int CalculateThreadCount(int pcSize)
        {
            int size = pcSize * System.Environment.ProcessorCount;
            size /= 100;

            return size > 0 ? size : 1;
        }
        #endregion

        #region Private methods
        private void Callback()
        {
            bool callbackEnd = false;
            var waitHandles = new WaitHandle[2] { _threadEnd, _threadQueueGate };

            while (!callbackEnd)
            {
                // wait for the sempahore to show available queued items or the termination event
                int eventIndex = EventWaitHandle.WaitAny(waitHandles);

                if (eventIndex == 0)
                {
                    callbackEnd = true;
                }
                else if (_threadStateQueue.Count > 0)
                {
                    // get the state item from the queue
                    IThreadPoolState threadState = null;
                    while (_threadStateQueue.Count > 0 && _threadStateQueue.TryDequeue(out threadState) && threadState != null)
                    {
                        try
                        {
                            // increment the number of running threads and make the callback
                            Interlocked.Increment(ref _activeThreads);
                            threadState.Callback(threadState.State);
                        }
                        catch (System.Exception)
                        {
                            // something went bad, we don't want the thread to terminate so we just eat it and count it
                            Interlocked.Increment(ref _threadExceptions);
                        }
                        finally
                        {
                            var taskState = threadState as TaskState;
                            if (taskState != null)
                            {
                                taskState.IsFinished = true;
                            }

                            // the thread has finished with the callback, so we are not active any more
                            Interlocked.Decrement(ref _activeThreads);

                            // the queued item is finished now, so decrement the count
                            Interlocked.Decrement(ref _queuedItems);
                        }
                    }
                }
            }
        }

        private void NotifyWaitingQueueEntry()
        {
            if (AvailableThreads > 0 && _threadStateQueue.Count > 0)
            {
                try
                {
                    _threadQueueGate.Release();
                }
                catch {}
            }
        }
        #endregion

        #region Public properties
        public int ActiveThreads { get { return _activeThreads; } }
        public int AvailableThreads { get { return _maxThreads - _activeThreads; } }
        public int MaxThreads { get { return _maxThreads; } }
        public int TotalExceptions { get { return _threadExceptions; } }
        public int QueueLength { get { return _threadStateQueue.Count; } }
        public int TotalQueueLength { get { return _queuedItems; } }
        public int MaxQueueLength { get { return _maxQueueLength; } }
        #endregion

        #region IDisposable Members
        public void Dispose()
        {
            // ask for the threads to terminate
            _threadEnd.Set();

            // we need to wait for the threads to shut down before we can free the end event and the gate semaphore
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                // wait for the threads to finish
                foreach (var thread in _threads)
                {
                    thread.Join();
                }

                _threadEnd.Close();

                _threadQueueGate.Close();
            });
        }
        #endregion
    }
}