﻿using System;
using System.Collections.Generic;
using System.Threading;

namespace MsieJavaScriptEngine
{
	/// <summary>
	/// Provides services for managing the queue of script tasks on the thread with increased stack size
	/// </summary>
	internal sealed class ScriptDispatcher : IDisposable
	{
#if !NETSTANDARD1_3
		/// <summary>
		/// The stack size is sufficient to run the code of modern JavaScript libraries
		/// </summary>
		const int SUFFICIENT_STACK_SIZE = 2 * 1024 * 1024;

#endif
		/// <summary>
		/// The thread with increased stack size
		/// </summary>
		private Thread _thread;

		/// <summary>
		/// Event to signal when the new script task entered to the queue
		/// </summary>
		private AutoResetEvent _waitHandle = new AutoResetEvent(false);

		/// <summary>
		/// Queue of script tasks
		/// </summary>
		private Queue<ScriptTask> _taskQueue = new Queue<ScriptTask>();

		/// <summary>
		/// Synchronizer of script task queue
		/// </summary>
		private readonly object _taskQueueSynchronizer = new object();

		/// <summary>
		/// Flag that object is destroyed
		/// </summary>
		private InterlockedStatedFlag _disposedFlag = new InterlockedStatedFlag();


		/// <summary>
		/// Constructs an instance of script dispatcher
		/// </summary>
		public ScriptDispatcher()
		{
#if NETSTANDARD1_3
			_thread = new Thread(StartThread)
#else
			_thread = new Thread(StartThread, SUFFICIENT_STACK_SIZE)
#endif
			{
				IsBackground = true
			};
			_thread.Start();
		}

		/// <summary>
		/// Destructs an instance of script dispatcher
		/// </summary>
		~ScriptDispatcher()
		{
			Dispose(false);
		}


		private void VerifyNotDisposed()
		{
			if (_disposedFlag.IsSet())
			{
				throw new ObjectDisposedException(ToString());
			}
		}

		/// <summary>
		/// Starts a thread with increased stack size.
		/// Loops forever, processing script tasks from the queue.
		/// </summary>
		private void StartThread()
		{
			while(true)
			{
				ScriptTask task = null;

				lock (_taskQueueSynchronizer)
				{
					if (_taskQueue.Count > 0)
					{
						task = _taskQueue.Dequeue();
						if (task == null)
						{
							return;
						}
					}
				}

				if (task != null)
				{
					try
					{
						task.Result = task.Delegate();
					}
					catch (Exception e)
					{
						task.Exception = e;
					}

					task.WaitHandle.Set();
				}
				else
				{
					_waitHandle.WaitOne();
				}
			}
		}

		/// <summary>
		/// Adds a script task to the end of the queue
		/// </summary>
		/// <param name="task">Script task</param>
		private void EnqueueTask(ScriptTask task)
		{
			lock (_taskQueueSynchronizer)
			{
				_taskQueue.Enqueue(task);
			}
			_waitHandle.Set();
		}

		/// <summary>
		/// Runs a specified delegate on the thread with increased stack size,
		/// and returns its result as an <see cref="System.Object"/>.
		/// Blocks until the invocation of delegate is completed.
		/// </summary>
		/// <param name="del">Delegate to invocation</param>
		/// <returns>Result of the delegate invocation</returns>
		private object InnnerInvoke(Func<object> del)
		{
			var waitHandle = new ManualResetEvent(false);
			var task = new ScriptTask(del, waitHandle);
			EnqueueTask(task);

			waitHandle.WaitOne();
			waitHandle.Dispose();

			if (task.Exception != null)
			{
				throw task.Exception;
			}

			return task.Result;
		}

		/// <summary>
		/// Runs a specified delegate on the thread with increased stack size,
		/// and returns its result as an <typeparamref name="T" />.
		/// Blocks until the invocation of delegate is completed.
		/// </summary>
		/// <typeparam name="T">The type of the return value of the method,
		/// that specified delegate encapsulates</typeparam>
		/// <param name="func">Delegate to invocation</param>
		/// <returns>Result of the delegate invocation</returns>
		public T Invoke<T>(Func<T> func)
		{
			VerifyNotDisposed();

			return (T)InnnerInvoke(() => func());
		}

		/// <summary>
		/// Runs a specified delegate on the thread with increased stack size.
		/// Blocks until the invocation of delegate is completed.
		/// </summary>
		/// <param name="action">Delegate to invocation</param>
		public void Invoke(Action action)
		{
			VerifyNotDisposed();

			InnnerInvoke(() =>
			{
				action();
				return null;
			});
		}

		#region IDisposable implementation

		/// <summary>
		/// Destroys object
		/// </summary>
		public void Dispose()
		{
			Dispose(true /* disposing */);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Destroys object
		/// </summary>
		/// <param name="disposing">Flag, allowing destruction of
		/// managed objects contained in fields of class</param>
		private void Dispose(bool disposing)
		{
			if (_disposedFlag.Set())
			{
				EnqueueTask(null);
				_thread.Join();

				if (_waitHandle != null)
				{
					_waitHandle.Dispose();
					_waitHandle = null;
				}

				if (disposing)
				{
					lock (_taskQueueSynchronizer)
					{
						if (_taskQueue != null)
						{
							_taskQueue.Clear();
						}
					}

					_thread = null;
				}
			}
		}

		#endregion

		#region Internal types

		/// <summary>
		/// Represents a script task, that must be executed on separate thread
		/// </summary>
		private sealed class ScriptTask
		{
			/// <summary>
			/// Gets a delegate to invocation
			/// </summary>
			public Func<object> Delegate
			{
				get;
				private set;
			}

			/// <summary>
			/// Gets a event to signal when the invocation of delegate has completed
			/// </summary>
			public ManualResetEvent WaitHandle
			{
				get;
				private set;
			}

			/// <summary>
			/// Gets or sets a result of the delegate invocation
			/// </summary>
			public object Result
			{
				get;
				set;
			}

			/// <summary>
			/// Gets or sets a exception, that occurred during the invocation of delegate.
			/// If no exception has occurred, this will be null.
			/// </summary>
			public Exception Exception
			{
				get;
				set;
			}


			/// <summary>
			/// Constructs an instance of script task
			/// </summary>
			/// <param name="del">Delegate to invocation</param>
			/// <param name="waitHandle">Event to signal when the invocation of delegate has completed</param>
			public ScriptTask(Func<object> del, ManualResetEvent waitHandle)
			{
				Delegate = del;
				WaitHandle = waitHandle;
			}
		}

		#endregion
	}
}