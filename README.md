[![Build Status](https://travis-ci.org/RipcordSoftware/ApplicationThreadPool.svg)](https://travis-ci.org/RipcordSoftware/ApplicationThreadPool)
[![Build status](https://ci.appveyor.com/api/projects/status/kl75ob7oytsqe3v3?svg=true)](https://ci.appveyor.com/project/craigminihan/applicationthreadpool)

# ApplicationThreadPool
An application thread pool for .NET or Mono.

Features:
* Set the number of threads in the pool at runtime
* Gives all the threads a useful name
* Create as many pools as you like
* Worker threads can be foreground or background
* You can set the thread priority of the workers

# Example
The short example below can be found in the examples directory in this repo.
```c#
static void Main(string[] args)
{
    var pool = new ApplicationThreadPool("test", 16, 1024, true);

    var activeThreads = pool.MaxThreads;
    for (var i = 0; i < pool.MaxThreads; ++i)
    {
        pool.QueueUserWorkItem(o => { Console.WriteLine("Hello"); Interlocked.Decrement(ref activeThreads); });
    }

    while (activeThreads > 0)
    {
        Thread.Sleep(0);
    }

    Console.WriteLine("Finished");
    Console.ReadLine();
}
```
