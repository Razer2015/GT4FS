using GT.Shared.Logging;
using System;
using System.Collections;
using System.Threading;

namespace GT.Shared.Threading {
    public class QueueWriter : IDisposable {
        private ManualResetEvent _manualResetEvent;
        private Thread _mainThread;
        private Queue _queue;
        private bool _isCanceled;
        private ILogWriter _logWriter;

        public QueueWriter(ILogWriter logWriter) {
            _manualResetEvent = new ManualResetEvent(false);
            _mainThread = new Thread(Run);
            _queue = Queue.Synchronized(new Queue());
            _logWriter = logWriter;
            _mainThread.Start();
        }

        private void Run() {
            while (true) {
                if (_queue.Count > 0) {
                    Write(_queue.Dequeue());
                }
                else {
                    if (_isCanceled) break;

                    _manualResetEvent.Reset();
                    _manualResetEvent.WaitOne();
                }
            }
        }

        private void Write(object obj) {
            try {
                _logWriter.WriteLine(obj.ToString());
            }
            catch (Exception e) {
                Enqueue(e);
            }
        }

        public void Enqueue(object obj) {
            _queue.Enqueue(obj);
            _manualResetEvent.Set();
        }

        public void Dispose() {
            _isCanceled = true;
            _manualResetEvent.Set();
        }
    }
}
