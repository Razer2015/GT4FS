using System;

namespace GT.Shared.Logging {
    public interface ILogWriter {
        void WriteLine(string message);
    }
}
