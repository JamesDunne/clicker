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
            Console.WriteLine("Done!");
        }

        const int samplesPerSec = 48000;   // samples/sec
        int ticksPerQuarter;
        double usecPerTick;
        int beatTicks;

        private void Run(string[] args)
        {
            Sequence seq = new Sequence(args[0]);

            // Grab meter and tempo changes from any track:
            var timeChanges =
                from tr in seq
                from ev in tr.Iterator()
                where ev.MidiMessage.MessageType == MessageType.Meta
                let mm = (MetaMessage)ev.MidiMessage
                where mm.MetaType == MetaType.TimeSignature || mm.MetaType == MetaType.Tempo
                orderby ev.AbsoluteTicks ascending
                select new { ev, mm };

            var lastEvent = (
                from tr in seq
                from ev in tr.Iterator()
                select new { ev, mm = (MetaMessage)null }
            ).Last();

            // Add the last event as a dummy time change event:
            timeChanges = timeChanges.Concat(Enumerable.Repeat(lastEvent, 1));

            // Ticks per quarter note:
            Console.WriteLine(seq.Division);

            // Create a default tempo of 120 bpm (500,000 us/b):
            var tcb = new TempoChangeBuilder() { Tempo = 500000 };
            tcb.Build();
            TempoMessage currentTempo = new TempoMessage(tcb.Result);

            // Create a default time signature of 4/4:
            var tsb = new TimeSignatureBuilder() { Numerator = 4, Denominator = 4 };
            tsb.Build();
            TimeSignatureMessage currentTimeSignature = new TimeSignatureMessage(tsb.Result);

            ticksPerQuarter = seq.Division;
            usecPerTick = (double)currentTempo.MicrosecondsPerQuarter / (double)ticksPerQuarter;
            beatTicks = (ticksPerQuarter * 4) / currentTimeSignature.Denominator;

            long lastSample = 0L;
            int lastTick = 0;

            foreach (var me in timeChanges)
            {
                for (int tick = lastTick, note = 0; tick < me.ev.AbsoluteTicks; tick += beatTicks, ++note)
                {
                    long sample = lastSample + samplesPerTick(tick - lastTick);
                    Console.WriteLine("{0,9}: {1}", sample, note % currentTimeSignature.Numerator);
                }

                lastSample += samplesPerTick(me.ev.AbsoluteTicks - lastTick);
                lastTick = me.ev.AbsoluteTicks;

                if (me.mm == null) continue;

                if (me.mm.MetaType == MetaType.Tempo)
                {
                    currentTempo = new TempoMessage(me.mm);
                    usecPerTick = (double)currentTempo.MicrosecondsPerQuarter / (double)ticksPerQuarter;
                    Console.WriteLine("{0,-13} {1,7}: {2,7} us/b = {3,6:0.00000000} bpm", me.mm.MetaType, me.ev.AbsoluteTicks, currentTempo.MicrosecondsPerQuarter, 500000d / currentTempo.MicrosecondsPerQuarter * 120);
                }
                else
                {
                    currentTimeSignature = new TimeSignatureMessage(me.mm);
                    beatTicks = (ticksPerQuarter * 4) / currentTimeSignature.Denominator;
                    Console.WriteLine("{0,-13} {1,7}: {2}/{3} = {4} ticks/beat", me.mm.MetaType, me.ev.AbsoluteTicks, currentTimeSignature.Numerator, currentTimeSignature.Denominator, beatTicks);
                }
            }
        }

        private long samplesPerTick(int ticks)
        {
            return (long)Math.Round(((usecPerTick * (double)samplesPerSec * (double)ticks) / 1000000d), 0, MidpointRounding.AwayFromZero);
        }

        private struct TimeSignatureMessage
        {
            private readonly MetaMessage msg;

            public TimeSignatureMessage(MetaMessage msg)
            {
                this.msg = msg;
            }

            public MetaMessage MetaMessage { get { return msg; } }

            public int Numerator { get { return (int)msg[0]; } }
            public int Denominator { get { return 1 << msg[1]; } }

            // NOTE: I ignore the other two bytes.

            /*
			     Message contains 4 bytes: 04 02 30 08
			     04 = four beats per measure
			     02 = 2^2 = 4 --> quarter note is the beat
			     30 = 48 decimal --> clock ticks per quarter note 
                      (tempo 60 beats per minute)
                 08 = 8 32nd notes per quarter note (beat)
            */
        }

        private struct TempoMessage
        {
            private readonly MetaMessage msg;

            public TempoMessage(MetaMessage msg)
            {
                this.msg = msg;
            }

            public MetaMessage MetaMessage { get { return msg; } }

            public int MicrosecondsPerQuarter { get { return (msg[0] << 16) | (msg[1] << 8) | msg[2]; } }
        }

        private struct AudioSample
        {
            private readonly int sample;

            public AudioSample(int sample)
            {
                this.sample = sample;
            }
        }
    }
}
