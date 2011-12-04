using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Clicker.Multimedia.Midi;
using System.Collections.Generic;

namespace Clicker
{
    class Program
    {
        static void Main(string[] args)
        {
            new Program().Run(args);
            Debug.WriteLine("Done!");
        }

        int samplesPerSec = 48000;      // samples/sec
        int channels = 1;               // mono
        int bitsPerSample = 16;         // 16-bit samples
        double samplesPerUsec;

        int ticksPerQuarter;
        double usecPerTick;
        int beatTicks;
        TimeSignatureMessage currentTimeSignature;
        TempoMessage currentTempo;
        double samplesPerTick;

        /// <summary>
        /// Controls whether or not dividing the meter is acceptable for the metronome
        /// </summary>
        bool forceClickDivision = false;
        /// <summary>
        /// Controls whether generated beats are attenuated if the meter is divided
        /// </summary>
        bool attenuateDividedBeats = false;
        /// <summary>
        /// Controls whether meter-proper off-beats are attenuated if they are smaller than the dividing meter threshold
        /// </summary>
        bool attenuateProperOffBeats = false;

        /// <summary>
        /// (1 &lt;&lt; value) is the minimum desired metronome meter if forceClickDivision is true.
        /// </summary>
        int clickOnDivision = 3;    // force click on 1/8th notes at minimum
                                    // 1 / (1 << value) == (1 / (2^value)) == 1/8th (when value is 3)

        private bool isDivided()
        {
            return (forceClickDivision && (clickOnDivision >= currentTimeSignature.DenominatorPower));
        }

        private int divisorPower()
        {
            return (clickOnDivision - currentTimeSignature.DenominatorPower);
        }

        private int reverseDivisorPower()
        {
            return (currentTimeSignature.DenominatorPower - clickOnDivision);
        }

        private bool doAttenuateBeat(int beat)
        {
            int meterBeat;
            int divisorpwr;

            if (!isDivided())
            {
                if (!attenuateProperOffBeats) return false;
                divisorpwr = reverseDivisorPower();
            }
            else
            {
                if (!attenuateDividedBeats) return false;
                divisorpwr = divisorPower();
            }

            // Chop off the LSBs that may have been introduced for the multiplier:
            meterBeat = (beat >> divisorpwr) << divisorpwr;
            return beat != meterBeat;
        }

        // TODO: double the numerator too?
        private void calcBeatTicks()
        {
            int clickDenominator = getDenominator();
            beatTicks = (ticksPerQuarter * 4) / clickDenominator;
        }

        private int getNumerator()
        {
            int num;
            if (isDivided())
                num = currentTimeSignature.Numerator << divisorPower();
            else
                num = currentTimeSignature.Numerator;
            return num;
        }

        private int getDenominator()
        {
            int clickDenominator;
            if (isDivided())
                clickDenominator = (1 << clickOnDivision);
            else
                clickDenominator = currentTimeSignature.Denominator;
            return clickDenominator;
        }

        private void calcUsecPerTick()
        {
            usecPerTick = (double)currentTempo.MicrosecondsPerQuarter / (double)ticksPerQuarter;
            samplesPerTick = usecPerTick * samplesPerUsec;
        }

        /// <summary>
        /// Main program.
        /// </summary>
        /// <param name="args"></param>
        private void Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine(
@"{0} [options ...] <path to MIDI.mid>

Description:
Clicker generates a WAVE file click track given a MIDI sequence with meter/key
and tempo change messages. The click track serves as a solid, sample-accurate
metronome that will line up with the MIDI sequence. You can import the
generated click track into any MIDI-friendly DAW tool such as SONAR, Cubase,
etc. to record with. You can even share the click track with other recording
artists working on your project to serve as a timebase to help synchronize work
across distances.

Author:     James S. Dunne http://bittwiddlers.org/
Copyright:  2011, bittwiddlers.org
Source:     http://github.com/JamesDunne/clicker

Options:
    -s <samplerate>      Set the output WAVE file's sample rate in Hz
                         (default 48000 Hz)
    -c <channels>        Set the output WAVE file's number of channels
                         (1 or 2, default 2)
    -d <click division>  Set the metronome to click on each (2^N)th note, 
                         scaling meter signatures appropriately to match.
                         (default: off, click on meter beats only)
    -ao                  Attenuate meter's off-beats if meter is faster
                         than the metronome. (default: off)
    -ad                  Attenuate inserted beats if metronome is clicking
                         faster than the meter. (default: off)
    <path to MIDI.mid>   Path to the MIDI arrangement to generate the click
                         track for.

Outputs:
    <path to MIDI.mid>.click.wav
", Process.GetCurrentProcess().ProcessName);
                return;
            }

            bool early = false;
            Queue<string> aq = new Queue<string>(args);
            while (!early && (aq.Count > 0))
            {
                string arg = aq.Peek();

                switch (arg.ToLower())
                {
                    case "-s":
                        aq.Dequeue();
                        if (!Int32.TryParse(aq.Dequeue(), out samplesPerSec))
                            samplesPerSec = 48000;
                        break;
                    case "-c":
                        aq.Dequeue();
                        if (!Int32.TryParse(aq.Dequeue(), out channels))
                            channels = 1;
                        break;
                    case "-d":
                        aq.Dequeue();
                        if (Int32.TryParse(aq.Dequeue(), out clickOnDivision))
                            forceClickDivision = true;
                        break;
                    case "-ao":
                        aq.Dequeue();
                        attenuateProperOffBeats = true;
                        break;
                    case "-ad":
                        aq.Dequeue();
                        attenuateDividedBeats = true;
                        break;
                    default:
                        early = true;
                        break;
                }
            }

            if (aq.Count < 1)
            {
                Console.WriteLine("Expected path to MIDI sequence.");
                return;
            }

            FileInfo midiFile = new FileInfo(aq.Dequeue());
            if (!midiFile.Exists)
            {
                Console.WriteLine("Could not find path '{0}'.", midiFile.FullName);
                return;
            }

            // Load the MIDI sequence:
            Sequence seq = new Sequence(midiFile.FullName);

            // Load our clicks (stereo 16-bit clips):
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
#if true
            byte[] pinghiraw = getAllBytes(asm.GetManifestResourceStream("Clicker.pinghi48k16b.raw"));
#else
            byte[] pinghiraw = File.ReadAllBytes("pinghi48k16b.raw");
#endif
            short[,] pinghi = new short[pinghiraw.Length / 4, 2];
            for (int i = 0, b = 0; i < pinghiraw.Length - 4; i += 4, ++b)
            {
                pinghi[b, 0] = unchecked((short)(pinghiraw[i + 0] | (pinghiraw[i + 1] << 8)));
                pinghi[b, 1] = unchecked((short)(pinghiraw[i + 2] | (pinghiraw[i + 3] << 8)));
            }
            int pinghiLength = pinghi.GetUpperBound(0) + 1;

#if true
            byte[] pingloraw = getAllBytes(asm.GetManifestResourceStream("Clicker.pinglo48k16b.raw"));
#else
            byte[] pingloraw = File.ReadAllBytes("pinglo48k16b.raw");
#endif
            short[,] pinglo = new short[pingloraw.Length / 4, 2];
            for (int i = 0, b = 0; i < pingloraw.Length - 4; i += 4, ++b)
            {
                pinglo[b, 0] = unchecked((short)(pingloraw[i + 0] | (pingloraw[i + 1] << 8)));
                pinglo[b, 1] = unchecked((short)(pingloraw[i + 2] | (pingloraw[i + 3] << 8)));
            }
            int pingloLength = pinglo.GetUpperBound(0) + 1;

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

            samplesPerUsec = (double)samplesPerSec / 1000000d;

            string outWaveFile = Path.Combine(midiFile.Directory.FullName, midiFile.Name + ".click.wav");
            Console.WriteLine("Writing click track to '{0}'", outWaveFile);

            var format = new WaveFormatChunk();
            format.dwSamplesPerSec = (uint)samplesPerSec;
            format.wChannels = (ushort)channels;
            format.wBitsPerSample = (ushort)bitsPerSample;
            Console.WriteLine(
                "Sample rate = {0,6} Hz; Channels = {1,1}; BitsPerSample = {2,2}",
                format.dwSamplesPerSec,
                format.wChannels,
                format.wBitsPerSample
            );

            // Open the WAVE for output:
            using (var wav = File.Open(outWaveFile, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var bs = new BufferedStream(wav))
            using (var bw = new BinaryWriter(bs))
            {
                var header = new WaveHeader();

                // Write the header
                bw.Write(header.sGroupID.ToCharArray());
                bw.Write(header.dwFileLength);
                bw.Write(header.sRiffType.ToCharArray());

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
                            // Start a click at this tick:
                            if (tick == nextBeatTick)
                            {
                                int beat = note;
                                //Debug.WriteLine("Click at tick {0,7}, sample {1,12:#######0.00}, beat {2,2}", tick, sample, beat);

                                // Copy in a click:
                                double vol = doAttenuateBeat(beat) ? 0.3d : 1d;

                                // Silence until start of this click:
                                int x = (int)((long)sample - (long)lastSample);
                                for (; x > 0; --x)
                                {
                                    for (int j = 0; j < channels; ++j)
                                        bw.Write((short)0);
                                }

                                // Choose the click sound based on the beat:
                                short[,] click = (beat == 0) ? pinglo : pinghi;
                                int clickLength = (beat == 0) ? pingloLength : pinghiLength;

                                // Write the portion of the click if we missed the start:
                                int samplesWritten = 0;
                                long delta = x;
                                for (x = -x; x < clickLength; ++x, ++samplesWritten)
                                {
                                    int y = (int)((double)x * 48000d / (double)samplesPerSec);
                                    if (y >= clickLength) break;

                                    for (int j = 0; j < channels; ++j)
                                        bw.Write((short)(click[y, j] * vol));
                                }

                                lastSample = sample + samplesWritten + delta;

                                // Set next beat tick:
                                nextBeatTick = tick + beatTicks;
                                note = (note + 1) % getNumerator();
                            }

                            sample += samplesPerTick;
                        }

                        if (haveKeyOrTempoChange)
                        {
                            if (me.mm.MetaType == MetaType.Tempo)
                            {
                                currentTempo = new TempoMessage(me.mm);
                                calcUsecPerTick();
                                Console.WriteLine(
                                    "{0,9}: tempo {1,8:###0.000} bpm = {2,9:#,###,##0} usec/qtr",
                                    me.ev.AbsoluteTicks,
                                    500000d / currentTempo.MicrosecondsPerQuarter * 120,
                                    currentTempo.MicrosecondsPerQuarter
                                );
                            }
                            else
                            {
                                currentTimeSignature = new TimeSignatureMessage(me.mm);
                                calcBeatTicks();
#if false
                                // NOTE: Assume key change is on a beat tick; force a reset of beats anyway.
                                //nextBeatTick = tick;
                                //note = 0;
#endif
                                Console.WriteLine(
                                    "{0,9}: meter {1,2}/{2,-2} treating as {3,2}/{4,-2}",
                                    me.ev.AbsoluteTicks,
                                    currentTimeSignature.Numerator, currentTimeSignature.Denominator,
                                    getNumerator(), getDenominator()
                                );
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

            Console.WriteLine("Click track written to '{0}'", outWaveFile);
            Console.WriteLine(
                "Sample rate = {0,6} Hz; Channels = {1,1}; BitsPerSample = {2,2}",
                format.dwSamplesPerSec,
                format.wChannels,
                format.wBitsPerSample
            );
        }

        private byte[] getAllBytes(Stream stream)
        {
            using (stream)
            {
                byte[] all = new byte[stream.Length];
                int nr = stream.Read(all, 0, (int)stream.Length);
                Debug.Assert(nr == (int)stream.Length);
                return all;
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
