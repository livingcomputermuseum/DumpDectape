using System;
using System.IO;
using System.IO.Ports;

//
// This program receives an Dectape image in 12, 16, or 18b format from the serial port, from the
// appropriate PDP8 TD8E dump program.  It is loosely based on David Gesswein's dumptd8e
// program.

//   This program should be running before the PDP8 end is started.
   
namespace DumpDectape
{
    class Program
    {
        public enum ReadState
        {            
            BlockFlag,
            Checksum,
            BlockData,
        }

        static void Main(string[] args)
        {

            if (!ReadConfiguration(args))
            {
                // Just exit, configuration is invalid.
                return;
            }

            //
            // Open the serial port.  Or try, anyway.
            //            
            SerialPort port = new SerialPort(_configuration.PortName, _configuration.BaudRate, _configuration.Parity, 8, _configuration.StopBits);
            port.Handshake = Handshake.None;            
            port.Open();

            string tapeFileName = _configuration.FileName;
            string diagFileName = Path.Combine(Path.GetDirectoryName(tapeFileName), Path.GetFileNameWithoutExtension(tapeFileName) + ".log");
           
            // Open the output file
            using (FileStream tapeOutput = new FileStream(tapeFileName, FileMode.Create, FileAccess.Write),
                              diagOutput = new FileStream(diagFileName, FileMode.Create, FileAccess.Write))
            {
                IBlockProcessor blockProcessor = null;
                int tapeBits = 0;                

                switch (_configuration.TapeType)
                {
                    case TapeType.Twelve:
                        blockProcessor = new BlockProcessor12(tapeOutput);
                        tapeBits = 12;
                        break;

                    case TapeType.Sixteen:
                        blockProcessor = new BlockProcessor16(tapeOutput);
                        tapeBits = 16;
                        break;

                    case TapeType.Eighteen:
                        blockProcessor = new BlockProcessor18(tapeOutput);
                        tapeBits = 18;
                        break;

                    default:
                        throw new InvalidOperationException("Unexpected tape type.");
                }
                 
                StreamWriter diagText = new StreamWriter(diagOutput);
                diagText.AutoFlush = true;
                diagText.WriteLine("Summary log for {0}-bit DECTape image {1} captured on {2}:", tapeBits, tapeFileName, DateTime.Now);
                
                Console.WriteLine("Initialized, waiting for tape data.");

                ReadState readState = ReadState.BlockFlag;
                int blockNumber = 0;
                int checksumByteIndex = 0;
                ushort temp = 0;
                int chksum12 = 0;
                int badBlocks = 0;

                while (true)
                {
                    byte[] buf = new byte[200];
                    int read = port.Read(buf, 0, buf.Length);

                    for (int i = 0; i < read; i++)
                    {
                        ulong b = buf[i];
                        switch (readState)
                        {
                            case ReadState.BlockFlag:
                                switch (b)
                                {
                                    case 0xff:
                                    case 0xfd:
                                        blockProcessor.StartBlock();
                                        readState = ReadState.BlockData;

                                        if (b == 0xfd)
                                        {
                                            Console.Write(" {0} bad ", blockNumber);
                                            diagText.WriteLine("Block {0} bad.", blockNumber);
                                            badBlocks++;
                                        }
                                        break;

                                    case 0xfe:
                                        readState = ReadState.Checksum;
                                        checksumByteIndex = 0;
                                        break;

                                    default:
                                        diagText.WriteLine("Error during transmission.");
                                        throw new InvalidOperationException("Missing start of block flag.  Aborting.");

                                }
                                break;

                            case ReadState.Checksum:
                                // read first byte of checksum
                                if (checksumByteIndex == 0)
                                {
                                    temp = (ushort)b;
                                    checksumByteIndex = 1;
                                }
                                else
                                {
                                    // last byte of checksum
                                    temp = (ushort)(temp | (b << 8));
                                    chksum12 = (temp + chksum12) & 0xfff;
                                    if (chksum12 != 0)
                                    {
                                        Console.WriteLine("Warning: Checksum error during read: {0}.", ToOctal(chksum12));
                                        diagText.WriteLine("Warning: Checksum error during read: {0}.", ToOctal(chksum12));
                                    }
                                    Console.WriteLine("Read completed {0}.  {1} blocks read, {2} bad blocks.", chksum12 == 0 ? "successfully" : "with checksum errors", blockNumber, badBlocks);
                                    diagText.WriteLine("Read completed {0}.  {1} blocks read, {2} bad blocks.", chksum12 == 0 ? "successfully" : "with checksum errors", blockNumber, badBlocks);
                                    return;
                                }
                                break;

                            case ReadState.BlockData:
                                //
                                // Process the current byte.
                                //
                                blockProcessor.ProcessByte(b);

                                //
                                // Deal with the 12-bit checksum.
                                //
                                switch (checksumByteIndex++)
                                {
                                    case 0:
                                        temp = (ushort)b;
                                        break;

                                    case 1:
                                        temp = (ushort)((temp | (b << 8)) & 0xfff);
                                        chksum12 = chksum12 + temp;
                                        temp = (ushort)(b >> 4);
                                        break;

                                    case 2:
                                        temp = (ushort)((temp | (b << 4)) & 0xfff);
                                        chksum12 = chksum12 + temp;
                                        checksumByteIndex = 0;
                                        break;

                                    default:
                                        throw new InvalidOperationException("Unexpected state when building 12-bit checksum.");
                                }

                                //
                                // Start next block when at end.
                                //
                                if (blockProcessor.BlockDone)
                                {
                                    readState = ReadState.BlockFlag;
                                    blockNumber++;
                                    checksumByteIndex = 0;
                                    if ((blockNumber % 5) == 0)
                                    {
                                        Console.Write("{0}", blockNumber);
                                    }
                                    else
                                    {
                                        Console.Write(".");
                                    }
                                }
                                break;
                        }
                    }
                }
            }
        }        

        public static ulong MangleBits(ulong mangled)
        {
            ulong unmangled;

            //
            // Reading 18-bit or 16-bit DECTapes on a 12-bit PDP-8 presents an interesting 
            // ordering of 6-bit half-words.
            // Effectively, each 36-bit sequence reorders 6-bit sequences as:
            // original: 5 4 3 2 1 0
            // mangled:  2 5 4 1 0 3
            // This routine moves things back to the right place in the 36-bit word.
            //
            unmangled =
                ((mangled & 0x00000003f) << 6) |
                ((mangled & 0x000000fc0) << 6) |
                ((mangled & 0x00003f000) << 18) |
                ((mangled & 0x000fc0000) >> 18) |
                ((mangled & 0x03f000000) >> 6) |
                ((mangled & 0xfc0000000) >> 6);

            return unmangled;
        }        

        public static string ToOctal(int i)
        {
            return Convert.ToString(i, 8);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("DumpDectape captures SIMH-compatible Dectape images from a PDP-8 system running the appropriate dump program.");
            Console.WriteLine("usage: DumpDectape -port <port> [-baud <baud>] [-stop <stop>] [-parity <parity>] -type <type> -file <filename>");
            Console.WriteLine("       <port> specifies the serial port to use (e.g. 'com1')");
            Console.WriteLine("       <baud> specifies the baud rate to use (e.g. '9600')");
            Console.WriteLine("       <stop> specifies the number of stop bits (e.g. '1')");
            Console.WriteLine("       <parity> specifies the parity (e.g. 'Even')");
            Console.WriteLine("       If unspecified, DumpDectape defaults to 9600 baud, 1 stop bit, no parity.");
            Console.WriteLine("       <type> specifies the type of Dectape being dumped: 18b, 16b, or 12b.");
            Console.WriteLine("       <filename> specifies the name of the SIMH image file to create.");
        }

        private static bool ReadConfiguration(string[] args)
        {
            _configuration = new Configuration();            

            int index = 0;
            while(index < args.Length)
            {
                try
                {
                    switch (args[index++])
                    {
                        case "-port":
                            _configuration.PortName = args[index++];
                            break;

                        case "-baud":
                            _configuration.BaudRate = int.Parse(args[index++]);
                            break;

                        case "-stop":
                            switch(args[index++])
                            {
                                case "1":
                                    _configuration.StopBits = StopBits.One;
                                    break;

                                case "2":
                                    _configuration.StopBits = StopBits.Two;
                                    break;

                                case "1.5":
                                    _configuration.StopBits = StopBits.OnePointFive;
                                    break;

                                default:
                                    throw new InvalidOperationException("Invalid Stop Bits specification.");
                            }
                            break;

                        case "-parity":
                            _configuration.Parity = (Parity)Enum.Parse(typeof(Parity), args[index++]);
                            break;

                        case "-type":
                            switch(args[index++])
                            {
                                case "12b":
                                    _configuration.TapeType = TapeType.Twelve;
                                    break;

                                case "16b":
                                    _configuration.TapeType = TapeType.Sixteen;
                                    break;

                                case "18b":
                                    _configuration.TapeType = TapeType.Eighteen;
                                    break;

                                default:
                                    throw new InvalidOperationException("Invalid Tape Type specification.");

                            }
                            break;

                        case "-file":
                            _configuration.FileName = args[index++];
                            break;

                        default:
                            throw new InvalidOperationException("Invalid option.");                            
                    }
                }
                catch
                {
                    // TODO: be more helpful about where the user screwed up.
                    PrintUsage();
                    return false;
                }
            }

            //
            // Ensure required options have been set, if not, print usage instructions.
            if (_configuration.TapeType == TapeType.Unspecified || 
                string.IsNullOrWhiteSpace(_configuration.PortName) || 
                string.IsNullOrWhiteSpace(_configuration.FileName))
            {
                PrintUsage();
                return false;
            }
            else
            {
                return true;
            }
        }

        private enum TapeType
        {
            Unspecified = 0,
            Twelve,
            Sixteen,
            Eighteen,
        }

        private class Configuration
        {
            public Configuration()
            {
                // Set things to defaults.
                FileName = null;
                PortName = null;
                BaudRate = 9600;
                Parity = Parity.None;
                StopBits = StopBits.One;
                TapeType = TapeType.Unspecified;
            }

            public string FileName;
            public string PortName;
            public int BaudRate;
            public Parity Parity;
            public StopBits StopBits;
            public TapeType TapeType;
        }

        private static Configuration _configuration;
    }

    public interface IBlockProcessor
    {
        void StartBlock();

        void ProcessByte(ulong b);

        bool BlockDone { get; }
    }

    public class BlockProcessor18 : IBlockProcessor
    {
        public BlockProcessor18(Stream outputStream)
        {
            _outStream = outputStream;
            _byteIndex36 = 0;
            _thirtySix = 0;
            _wordCount18 = 0;
            _blockDone = true;
        }

        public bool BlockDone
        {
            get { return _blockDone; }
        }

        public void StartBlock()
        {
            _blockDone = false;
            _byteIndex36 = 0;
            _wordCount18 = 0;
            _thirtySix = 0;
        }

        /// <summary>
        /// Appends a new byte onto the output data.
        /// </summary>
        /// <param name="b"></param>
        /// <returns>true if the end of the block has been reached, false otherwise</returns>
        public void ProcessByte(ulong b)
        {
            if (_blockDone)
            {
                throw new InvalidOperationException("Invalid state: ProcessByte called before StartBlock.");
            }

            //
            // For 18-bit tapes:
            // There are 384 12-bit words per block and each 12-bit word is
            // transferred as 1.5 bytes.  The 384 12-bit words in a block 
            // correspond to 256 18-bit words (1.5 12-bit words per 18-bit word)
            // which we want to write out to an 18-bit SIMH dectape image.  This 
            // is further complicated by the fact that due to the way the 
            // bits actually get written to the tape, reading 18-bit words 
            // as 1.5 12-bit words ends up transposing 6-bit segments inside
            // a 36-bit segment:
            // If the original 6-bit half-word ordering is:
            //          5 4 3 2 1 0
            // When read back into sequential 12-bit words, these half-words get 
            // rearranged:
            //          2 5 4 1 0 3
            //
            // To keep things somewhat easy to understand here (so as to preserve
            // my own sanity) at the expense of slightly longer code, 
            // the state machine below parses 9 bytes (72 bits) at a time
            // which corresponds to 2 36-bit words (4 18-bit words) and re-arranges the 6-bit
            // half-words after each 36-bit word is read in.  These re-arranged words are then
            // pushed out to disk in standard SIMH 18b format.
            //
            switch (_byteIndex36++)
            {
                case 0:
                    _thirtySix = b;
                    break;

                case 1:
                    _thirtySix |= (ulong)(b << 8);
                    break;

                case 2:
                    _thirtySix |= (ulong)(b << 16);
                    break;

                case 3:
                    _thirtySix |= (ulong)(b << 24);
                    break;

                case 4:
                    _thirtySix |= (ulong)((b & 0xf) << 32);

                    // First 36-bit word finished.
                    WriteThirtySix(_outStream, Program.MangleBits(_thirtySix));
                    _wordCount18 += 2;

                    // Start next 36-bit word
                    _thirtySix = (ulong)((b & 0xf0) >> 4);
                    break;

                case 5:
                    _thirtySix |= (ulong)(b << 4);
                    break;

                case 6:
                    _thirtySix |= (ulong)(b << 12);
                    break;

                case 7:
                    _thirtySix |= (ulong)(b << 20);
                    break;

                case 8:
                    _thirtySix |= (ulong)(b << 28);

                    // Second 36-bit word finished.
                    WriteThirtySix(_outStream, Program.MangleBits(_thirtySix));
                    _wordCount18 += 2;

                    _byteIndex36 = 0;
                    break;

                default:
                    throw new InvalidOperationException("Unexpected state when building 36-bit word.");

            }

            if (_wordCount18 == 256)
            {
                // Finished with the current block, reset our word count
                // and let the caller know we're done.

                _blockDone = true;
            }
        }

        private static void WriteThirtySix(Stream outStream, ulong thirtySix)
        {
            WriteEighteen(outStream, (uint)(thirtySix & 0x3ffff));
            WriteEighteen(outStream, (uint)((thirtySix & 0xffffc0000) >> 18));
        }

        private static void WriteEighteen(Stream outStream, uint eighteen)
        {
            outStream.WriteByte((byte)(eighteen & 0x000000ff));
            outStream.WriteByte((byte)((eighteen & 0x0000ff00) >> 8));
            outStream.WriteByte((byte)((eighteen & 0x00ff0000) >> 16));
            outStream.WriteByte((byte)((eighteen & 0xff000000) >> 24));

            outStream.Flush();
        }

        /// <summary>
        /// The current byte index into the two 36-bit words we're building
        /// </summary>
        private int _byteIndex36;

        /// <summary>
        /// The current 36-bit word we've built
        /// </summary>
        private ulong _thirtySix;

        /// <summary>
        /// The number of 18-bit words we've read.
        /// </summary>
        private int _wordCount18;

        /// <summary>
        /// Whether we've completed a block (and StartBlock must be called before processing more data)
        /// </summary>
        private bool _blockDone;

        /// <summary>
        /// The stream we'll flush the data to.
        /// </summary>
        private Stream _outStream;
    }

    public class BlockProcessor16 : IBlockProcessor
    {
        public BlockProcessor16(Stream outputStream)
        {
            _outStream = outputStream;
            _byteIndex36 = 0;
            _thirtySix = 0;
            _wordCount16 = 0;
            _blockDone = true;
        }

        public bool BlockDone
        {
            get { return _blockDone; }
        }

        public void StartBlock()
        {
            _blockDone = false;
            _byteIndex36 = 0;
            _wordCount16 = 0;
            _thirtySix = 0;
        }

        /// <summary>
        /// Appends a new byte onto the output data.
        /// </summary>
        /// <param name="b"></param>
        /// <returns>true if the end of the block has been reached, false otherwise</returns>
        public void ProcessByte(ulong b)
        {
            if (_blockDone)
            {
                throw new InvalidOperationException("Invalid state: ProcessByte called before StartBlock.");
            }

            //
            // For 16-bit tapes:
            // See the comments in BlockProcessor18.ProcessByte for the nitty-gritty.
            // 16-bit tapes are handled nearly identically, since the format on tape is identical.
            // The difference is that 16-bit dectapes do not use the upper 2 bits of each 
            // 18-bit word on tape.  After the typical 18-bit processing and re-arrangement, we write out the data
            // in the standard 16-bit SIMH format.
            // Some of this code could technically be factored out.
            //
            switch (_byteIndex36++)
            {
                case 0:
                    _thirtySix = b;
                    break;

                case 1:
                    _thirtySix |= (ulong)(b << 8);
                    break;

                case 2:
                    _thirtySix |= (ulong)(b << 16);
                    break;

                case 3:
                    _thirtySix |= (ulong)(b << 24);
                    break;

                case 4:
                    _thirtySix |= (ulong)((b & 0xf) << 32);

                    // First 36-bit word finished.
                    WriteThirtySix(_outStream, Program.MangleBits(_thirtySix));
                    _wordCount16 += 2;

                    // Start next 36-bit word
                    _thirtySix = (ulong)((b & 0xf0) >> 4);
                    break;

                case 5:
                    _thirtySix |= (ulong)(b << 4);
                    break;

                case 6:
                    _thirtySix |= (ulong)(b << 12);
                    break;

                case 7:
                    _thirtySix |= (ulong)(b << 20);
                    break;

                case 8:
                    _thirtySix |= (ulong)(b << 28);

                    // Second 36-bit word finished.
                    WriteThirtySix(_outStream, Program.MangleBits(_thirtySix));
                    _wordCount16 += 2;

                    _byteIndex36 = 0;
                    break;

                default:
                    throw new InvalidOperationException("Unexpected state when building 36-bit word.");

            }

            if (_wordCount16 == 256)
            {
                // Finished with the current block, reset our word count
                // and let the caller know we're done.

                _blockDone = true;
            }
        }

        private static void WriteThirtySix(Stream outStream, ulong thirtySix)
        {
            WriteSixteen(outStream, (uint)(thirtySix & 0xffff));

            // TODO: verify this is correct.
            WriteSixteen(outStream, (uint)((thirtySix & 0x3fffc0000) >> 18));
        }

        private static void WriteSixteen(Stream outStream, uint sixteen)
        {
            outStream.WriteByte((byte) (sixteen & 0x00ff));
            outStream.WriteByte((byte)((sixteen & 0xff00) >> 8));
            outStream.Flush();
        }

        /// <summary>
        /// The current byte index into the two 36-bit words we're building
        /// </summary>
        private int _byteIndex36;

        /// <summary>
        /// The current 36-bit word we've built
        /// </summary>
        private ulong _thirtySix;

        /// <summary>
        /// The number of 18-bit words we've read.
        /// </summary>
        private int _wordCount16;

        /// <summary>
        /// Whether we've completed a block (and StartBlock must be called before processing more data)
        /// </summary>
        private bool _blockDone;

        /// <summary>
        /// The stream we'll flush the data to.
        /// </summary>
        private Stream _outStream;
    }

    public class BlockProcessor12 : IBlockProcessor
    {
        public BlockProcessor12(Stream outputStream)
        {
            _outStream = outputStream;
            _byteIndex12 = 0;
            _twelve = 0;
            _wordCount12 = 0;
            _blockDone = true;
        }

        public bool BlockDone
        {
            get { return _blockDone; }
        }

        public void StartBlock()
        {
            _blockDone = false;
            _byteIndex12 = 0;
            _twelve = 0;
            _wordCount12 = 0;
        }

        /// <summary>
        /// Appends a new byte onto the output data.
        /// </summary>
        /// <param name="b"></param>
        /// <returns>true if the end of the block has been reached, false otherwise</returns>
        public void ProcessByte(ulong b)
        {
            if (_blockDone)
            {
                throw new InvalidOperationException("Invalid state: ProcessByte called before StartBlock.");
            }

            //
            // For 12-bit tapes:
            // There are 129 12-bit words per block and each 12-bit word is
            // transferred as 1.5 bytes.  The reassembled 12-bit words are then
            // pushed out to disk in standard SIMH 12b format.
            //
            switch (_byteIndex12++)
            {
                case 0:
                    _twelve = b;
                    break;

                case 1:
                    _twelve |= (ulong)(b << 8);

                    // First 12-bit word finished.
                    WriteTwelve(_outStream, _twelve);
                    _wordCount12++;

                    // Start next 12-bit word.
                    _twelve = (ulong)(b >> 4);
                    break;

                case 2:
                    // Second 12-bit word finished.
                    _twelve |= (ulong)((b << 4));
                    WriteTwelve(_outStream, _twelve);
                    _wordCount12++;

                    // Start over.
                    _byteIndex12 = 0;
                    break;

                default:
                    throw new InvalidOperationException("Unexpected state when building 12-bit word.");
            }

            if (_wordCount12 == 129)
            {
                // Finished with the current block.
                _blockDone = true;
            }
        }

        private static void WriteTwelve(Stream outStream, ulong twelve)
        {        
            outStream.WriteByte((byte) (twelve & 0x0ff));
            outStream.WriteByte((byte)((twelve & 0xf00) >> 8));
            outStream.Flush();
        }

        /// <summary>
        /// The current byte index into the two 36-bit words we're building
        /// </summary>
        private int _byteIndex12;

        /// <summary>
        /// The current 36-bit word we've built
        /// </summary>
        private ulong _twelve;

        /// <summary>
        /// The number of 18-bit words we've read.
        /// </summary>
        private int _wordCount12;

        /// <summary>
        /// Whether we've completed a block (and StartBlock must be called before processing more data)
        /// </summary>
        private bool _blockDone;

        /// <summary>
        /// The stream we'll flush the data to.
        /// </summary>
        private Stream _outStream;
    }
}
