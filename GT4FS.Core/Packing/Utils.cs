using System;
using System.Collections.Generic;
using System.Text;

namespace GT4FS.Core.Packing
{
    public class Utils
    {
        public static int Align(int offset, int alignment)
        {
            var newPos = (-offset % alignment + alignment) % alignment;
            offset += newPos;
            return offset;
        }
    }
}
