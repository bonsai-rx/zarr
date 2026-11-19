using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents a writer that writes each element on a dedicated thread, which flushes whenever
    /// no element is waiting and closes once every queued element is written.
    /// </summary>
    /// <remarks>
    /// Elements are queued by reference and the queue has no limit. No method waits for the writer
    /// thread.
    /// </remarks>
    /// <typeparam name="T">The type of the elements to write.</typeparam>
    internal sealed class ZarrBackgroundWriter<T>
    {
        readonly ConcurrentQueue<T> queue = new ConcurrentQueue<T>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Action<T> write;
        readonly Action flush;
        readonly Action close;
        readonly Action<Exception> onError;
        readonly Action onClosed;
        int sleeping;
        volatile bool completed;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrBackgroundWriter{T}"/> class and starts
        /// its writer thread.
        /// </summary>
        /// <param name="write">The action that writes a single element on the writer thread.</param>
        /// <param name="flush">
        /// The action that writes any buffered data on the writer thread when no element is waiting.
        /// </param>
        /// <param name="close">
        /// The action that releases the resources used for writing, called once on the writer
        /// thread after the last element, even if an error occurred.
        /// </param>
        /// <param name="onError">
        /// The action called on the writer thread with the first error raised while writing or
        /// closing. No element is written after an error.
        /// </param>
        /// <param name="onClosed">The action called last on the writer thread.</param>
        public ZarrBackgroundWriter(Action<T> write, Action flush, Action close, Action<Exception> onError, Action onClosed)
        {
            this.write = write;
            this.flush = flush;
            this.close = close;
            this.onError = onError;
            this.onClosed = onClosed;
            var thread = new Thread(Run) { IsBackground = true, Name = nameof(ZarrBackgroundWriter<T>) };
            thread.Start();
        }

        /// <summary>
        /// Queues an element to be written. Elements queued after <see cref="Complete"/> is called
        /// are ignored.
        /// </summary>
        /// <param name="value">The element to write.</param>
        public void Write(T value)
        {
            if (completed) return;
            queue.Enqueue(value);
            Wake();
        }

        /// <summary>
        /// Stops accepting elements and closes the writer once every queued element is written.
        /// </summary>
        public void Complete()
        {
            completed = true;
            Wake();
        }

        void Wake()
        {
            if (Interlocked.CompareExchange(ref sleeping, 0, 1) == 1)
            {
                signal.Set();
            }
        }

        void Run()
        {
            Exception? error = null;
            while (error == null)
            {
                try
                {
                    while (queue.TryDequeue(out var value))
                    {
                        write(value);
                    }

                    flush();
                }
                catch (Exception ex)
                {
                    error = ex;
                    completed = true;
                    break;
                }

                Interlocked.Exchange(ref sleeping, 1);
                if (queue.IsEmpty)
                {
                    if (completed) break;
                    signal.WaitOne();
                }

                Interlocked.Exchange(ref sleeping, 0);
            }

            try { close(); }
            catch (Exception ex) { error ??= ex; }

            if (error != null) onError(error);
            onClosed();
        }
    }
}
