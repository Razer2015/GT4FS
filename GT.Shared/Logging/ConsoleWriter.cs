using System;

namespace GT.Shared.Logging {
    public class ConsoleWriter : ILogWriter {
        public void WriteLine(string message) {
            Console.WriteLine(message);
        }

        public void WriteLine(string message, params object[] parameters) {
            Console.WriteLine(message, parameters);
        }

        public void WriteLine(Exception exception) {
            Console.WriteLine(exception);
        }
    }
}
