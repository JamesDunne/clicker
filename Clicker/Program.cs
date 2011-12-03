using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sanford.Multimedia.Midi;
using System.IO;
using System.Diagnostics;

namespace Clicker
{
    class Program
    {
        static void Main(string[] args)
        {
            new Program().Run(args);
            Console.WriteLine("Done!");
        }

        const int samplesPerSec = 48000;    // samples/sec

        const bool forceClickDivision = true;
        const int clickOnDivision = 3;      // force click on 8th notes at minimum
                                            // 1 / (1 << value) == (1 / (2^value)) == 1/8th (when value is 3)

        const double samplesPerUsec = (double)samplesPerSec / 1000000d;

        int ticksPerQuarter;
        double usecPerTick;
        int beatTicks;
        TimeSignatureMessage currentTimeSignature;
        TempoMessage currentTempo;
        double samplesPerTick;

        // TODO: double the numerator too?
        private void calcBeatTicks()
        {
            int clickDenominator;
            if (forceClickDivision && (clickOnDivision >= currentTimeSignature.DenominatorPower))
                clickDenominator = (1 << clickOnDivision);
            else
                clickDenominator = currentTimeSignature.Denominator;

            beatTicks = (ticksPerQuarter * 4) / clickDenominator;
        }

        private int getNumerator()
        {
            int num;
            if (forceClickDivision && (clickOnDivision >= currentTimeSignature.DenominatorPower))
                num = currentTimeSignature.Numerator << (clickOnDivision - currentTimeSignature.DenominatorPower);
            else
                num = currentTimeSignature.Numerator;
            return num;
        }

        private void calcUsecPerTick()
        {
            usecPerTick = (double)currentTempo.MicrosecondsPerQuarter / (double)ticksPerQuarter;
            samplesPerTick = usecPerTick * samplesPerUsec;
        }

        private void Run(string[] args)
        {
            // Load our clicks (stereo 16-bit clips):
            byte[] pinghiraw = File.ReadAllBytes("pinghi48k16b.raw");
            short[,] pinghi = new short[pinghiraw.Length / 4, 2];
            for (int i = 0, b = 0; i < pinghiraw.Length - 4; i += 4, ++b)
            {
                pinghi[b, 0] = unchecked((short)(pinghiraw[i + 0] | (pinghiraw[i + 1] << 8)));
                pinghi[b, 1] = unchecked((short)(pinghiraw[i + 2] | (pinghiraw[i + 3] << 8)));
            }
            int pinghiLength = pinghi.GetUpperBound(0) + 1;

            byte[] pingloraw = File.ReadAllBytes("pinglo48k16b.raw");
            short[,] pinglo = new short[pingloraw.Length / 4, 2];
            for (int i = 0, b = 0; i < pingloraw.Length - 4; i += 4, ++b)
            {
                pinglo[b, 0] = unchecked((short)(pingloraw[i + 0] | (pingloraw[i + 1] << 8)));
                pinglo[b, 1] = unchecked((short)(pingloraw[i + 2] | (pingloraw[i + 3] << 8)));
            }
            int pingloLength = pinglo.GetUpperBound(0) + 1;

            // Load the MIDI sequence:
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
                orderby ev.AbsoluteTicks ascending
                select ev
            ).Last();

            // Ticks per quarter note:
            Console.WriteLine(seq.Division);

            // Create a default tempo of 120 bpm (500,000 us/b):
            var tcb = new TempoChangeBuilder() { Tempo = 500000 };
            tcb.Build();
            currentTempo = new TempoMessage(tcb.Result);

            // Create a default time signature of 4/4:
            var tsb = new TimeSignatureBuilder() { Numerator = 4, Denominator = 4 };
            tsb.Build();
            currentTimeSignature = new TimeSignatureMessage(tsb.Result);

            ticksPerQuarter = seq.Division;
            calcUsecPerTick();
            calcBeatTicks();

            double sample = 0d;

            using (var wav = File.Open("click.wav", FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var bs = new BufferedStream(wav))
            using (var bw = new BinaryWriter(bs))
            {
                var header = new WaveHeader();

                // Write the header
                bw.Write(header.sGroupID.ToCharArray());
                bw.Write(header.dwFileLength);
                bw.Write(header.sRiffType.ToCharArray());

                var format = new WaveFormatChunk();
                format.dwSamplesPerSec = samplesPerSec;
                format.wChannels = 2;
                format.wBitsPerSample = 16;

                // Write the format chunk
                bw.Write(format.sChunkID.ToCharArray());
                bw.Write(format.dwChunkSize);
                bw.Write(format.wFormatTag);
                bw.Write(format.wChannels);
                bw.Write(format.dwSamplesPerSec);
                bw.Write(format.dwAvgBytesPerSec);
                bw.Write(format.wBlockAlign);
                bw.Write(format.wBitsPerSample);

                var data = new WaveDataChunk();

                // Write the data chunk
                bw.Write(data.sChunkID.ToCharArray());
                bw.Write(data.dwChunkSize);

                double lastSample = sample;
                int nextBeatTick = 0;
                int note = 0;
                int tick = 0;

                using (var en = timeChanges.GetEnumerator())
                {
                    MidiEvent nextEvent;
                    bool haveKeyOrTempoChange = en.MoveNext();
                    var me = en.Current;
                    nextEvent = me.ev;

                    while (tick < lastEvent.AbsoluteTicks)
                    {
                        for (; tick < nextEvent.AbsoluteTicks; ++tick)
                        {
                            sample += samplesPerTick;

                            // Start a click at this tick:
                            if (tick == nextBeatTick)
                            {
                                int beat = note;
                                //Console.WriteLine("Click at tick {0,7}, sample {1,12:#######0.00}, beat {2,2}", tick, sample, beat);

                                // Copy in a click:

                                // Silence until start of this click:
                                long x = (long)sample - (long)lastSample;
                                for (; x > 0; --x)
                                {
                                    for (int j = 0; j < format.wChannels; ++j)
                                        bw.Write((short)0);
                                }

                                // Choose the click sound based on the beat:
                                short[,] click = (beat == 0) ? pinglo : pinghi;
                                int clickLength = (beat == 0) ? pingloLength : pinghiLength;

                                // Write the portion of the click if we missed the start:
                                long delta = x;
                                for (x = -x; x < clickLength; ++x)
                                {
                                    for (int j = 0; j < format.wChannels; ++j)
                                        bw.Write(click[x, j]);
                                }

                                lastSample = sample + clickLength + delta;
                                Debug.Assert(Math.Abs((long)lastSample - wav.Length) <= 1);

                                // Set next beat tick:
                                nextBeatTick = tick + beatTicks;
                                note = (note + 1) % getNumerator();
                            }
                        }

                        if (haveKeyOrTempoChange)
                        {
                            if (me.mm.MetaType == MetaType.Tempo)
                            {
                                currentTempo = new TempoMessage(me.mm);
                                calcUsecPerTick();
                                Console.WriteLine("{0,-13} {1,7}: {2,7} us/b = {3,6:##0.00} bpm", me.mm.MetaType, me.ev.AbsoluteTicks, currentTempo.MicrosecondsPerQuarter, 500000d / currentTempo.MicrosecondsPerQuarter * 120);
                            }
                            else
                            {
                                currentTimeSignature = new TimeSignatureMessage(me.mm);
                                calcBeatTicks();
                                // NOTE: Assume key change is on a beat tick; force a reset of beats anyway.
                                nextBeatTick = tick;
                                note = 0;
                                Console.WriteLine("{0,-13} {1,7}: {2}/{3} = {4} ticks/beat", me.mm.MetaType, me.ev.AbsoluteTicks, currentTimeSignature.Numerator, currentTimeSignature.Denominator, beatTicks);
                            }

                            haveKeyOrTempoChange = en.MoveNext();
                            if (haveKeyOrTempoChange)
                            {
                                me = en.Current;
                                nextEvent = me.ev;
                            }
                            else
                            {
                                me = null;
                                nextEvent = lastEvent;
                            }
                        }
                    }
                }

                // Write RIFF file size:
                bw.Seek(4, SeekOrigin.Begin);
                uint filesize = (uint)wav.Length;
                bw.Write(filesize - 8);

                // Write "data" chunk size:
                bw.Seek(0x28, SeekOrigin.Begin);
                bw.Write(filesize - 0x2C);
            }
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
            public int DenominatorPower { get { return (int)msg[1]; } }

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

        private sealed class WaveHeader
        {
            public string sGroupID; // RIFF
            public uint dwFileLength; // total file length minus 8, which is taken up by RIFF
            public string sRiffType; // always WAVE

            /// <summary>
            /// Initializes a WaveHeader object with the default values.
            /// </summary>
            public WaveHeader()
            {
                dwFileLength = 0;
                sGroupID = "RIFF";
                sRiffType = "WAVE";
            }
        }

        private sealed class WaveFormatChunk
        {
            public string sChunkID;         // Four bytes: "fmt "
            public uint dwChunkSize;        // Length of header in bytes
            public ushort wFormatTag;       // 1 (MS PCM)
            public ushort wChannels;        // Number of channels
            public uint dwSamplesPerSec;    // Frequency of the audio in Hz... 44100
            public uint dwAvgBytesPerSec    // for estimating RAM allocation
            {
                get { return dwSamplesPerSec * wBlockAlign; }
            }
            public ushort wBlockAlign       // sample frame size, in bytes
            {
                get { return (ushort)(wChannels * (wBitsPerSample / 8)); }
            }
            public ushort wBitsPerSample;   // bits per sample

            /// <summary>
            /// Initializes a format chunk with the following properties:
            /// Sample rate: 48000 Hz
            /// Channels: Stereo
            /// Bit depth: 16-bit
            /// </summary>
            public WaveFormatChunk()
            {
                sChunkID = "fmt ";
                dwChunkSize = 16;
                wFormatTag = 1;
                wChannels = 2;
                dwSamplesPerSec = 48000;
                wBitsPerSample = 16;
            }
        }

        private sealed class WaveDataChunk
        {
            public string sChunkID;     // "data"
            public uint dwChunkSize;    // Length of header in bytes

            /// <summary>
            /// Initializes a new data chunk with default values.
            /// </summary>
            public WaveDataChunk()
            {
                dwChunkSize = 0;
                sChunkID = "data";
            }
        }
    }
}
