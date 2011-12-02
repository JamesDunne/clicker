using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sanford.Multimedia.Midi;

namespace Clicker
{
    class Program
    {
        static void Main(string[] args)
        {
            new Program().Run(args);
        }

        private void Run(string[] args)
        {
            Sequence seq = new Sequence(args[0]);
            Console.WriteLine("Done!");
        }
    }
}
