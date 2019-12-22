using GT4FS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GT4FS.Tester {
    class Program {
        static void Main(string[] args) {
            //new Volume(@"L:\GranTurismo_Discs\Gran Turismo 4\PAL\GT4.VOL").Read();
            //new Volume(@"L:\GranTurismo_Discs\Gran Turismo HD\EP9000_NPEA90002_00_GTHD_20\NPEA90002\USRDIR\GT4.VOL").Read();
            new Volume(@"L:\GranTurismo_Discs\Gran Turismo 4\PAL\GT4L1.VOL").Read();
            // new Volume(@"L:\GranTurismo_Discs\Gran Turismo 4\PAL\GT4L1.VOL").Read();
        }
    }
}
