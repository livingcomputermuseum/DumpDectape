using System;
using System.IO;
using System.IO.Ports;

/* 
 * This program receives an 18-bit Dectape image from the serial port from the
   PDP8 TD8E dump program.  It is loosely based on David Gesswein's dumptd8e
   program.

   This program should be running before the PDP8 end is started.
   
*/
namespace DumpDT18
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

            if (args.Length < 3)
            {
                Console.WriteLine("usage: DumpDT18 <port> <baud> <filename>");
                return;
            }

            //
            // Open the serial port.  Or try, anyway.
            //            
            SerialPort port = new SerialPort(args[0], int.Parse(args[1]));
            port.StopBits = StopBits.One;
            port.Handshake = Handshake.None;
            port.Parity = Parity.None;
            port.Open();

            string tapeFileName = args[2];
            string diagFileName = Path.Combine(Path.GetDirectoryName(tapeFileName), Path.GetFileNameWithoutExtension(tapeFileName) + ".log");
           
            // Open the output file
            using (FileStream tapeOutput = new FileStream(tapeFileName, FileMode.Create, FileAccess.Write),
                              diagOutput = new FileStream(diagFileName, FileMode.Create, FileAccess.Write))
            {
                StreamWriter diagText = new StreamWriter(diagOutput);
                diagText.AutoFlush = true;
                diagText.WriteLine("Summary log for 18-bit DECTape image {0} captured on {1}:", tapeFileName, DateTime.Now);
                
                Console.WriteLine("Initialized, waiting for tape data.");

                ReadState readState = ReadState.BlockFlag;
                int wordCount18 = 0;
                int blockNumber = 0;
                int byteIndex36 = 0;
                int byteIndex12 = 0;
                ushort temp = 0;
                int chksum12 = 0;
                ulong thirtySix = 0;
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
                                        wordCount18 = 0;
                                        byteIndex36 = 0;
                                        readState = ReadState.BlockData;

                                        if (b == 0xfd)
                                        {
                                            Console.WriteLine("\nBlock {0} bad\n", blockNumber);
                                            diagText.WriteLine("Block {0} bad.", blockNumber);
                                            badBlocks++;
                                        }
                                        break;

                                    case 0xfe:
                                        readState = ReadState.Checksum;
                                        byteIndex12 = 0;
                                        break;

                                    default:
                                        diagText.WriteLine("Error during transmission.");
                                        throw new InvalidOperationException("Missing start of block flag.  Aborting.");

                                }
                                break;

                            case ReadState.Checksum:
                                // read first byte of checksum
                                if (byteIndex12 == 0)
                                {
                                    temp = (ushort)b;
                                    byteIndex12 = 1;
                                }
                                else
                                {
                                    // last byte of checksum
                                    temp = (ushort)(temp | (b << 8));
                                    chksum12 = (temp + chksum12) & 0xfff;
                                    if (chksum12 != 0)
                                    {
                                        Console.WriteLine("Read completed.  Warning: Checksum error: {0}.", ToOctal(chksum12));
                                        diagText.WriteLine("Checksum error: {0}", ToOctal(chksum12));
                                        return;
                                    }
                                    Console.WriteLine("Read completed successfully.  {0} blocks read, {1} bad blocks.", blockNumber, badBlocks);
                                    diagText.WriteLine("Read successful.  {0} blocks read, {1} bad blocks.", blockNumber, badBlocks);
                                    return;
                                }
                                break;

                            case ReadState.BlockData:

                                //
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
                                // We do this while ALSO summing the 12-bit checksum used at the end of the transfer.
                                //
                                switch (byteIndex36++)
                                {
                                    case 0:
                                        thirtySix = b;
                                        break;

                                    case 1:
                                        thirtySix |= (ulong)(b << 8);
                                        break;

                                    case 2:
                                        thirtySix |= (ulong)(b << 16);
                                        break;

                                    case 3:
                                        thirtySix |= (ulong)(b << 24);
                                        break;

                                    case 4:
                                        thirtySix |= (ulong)((b & 0xf) << 32);

                                        // First 36-bit word finished.
                                        WriteThirtySix(tapeOutput, MangleBits(thirtySix));
                                        wordCount18 += 2;

                                        // Start next 36-bit word
                                        thirtySix = (ulong)((b & 0xf0) >> 4);
                                        break;

                                    case 5:
                                        thirtySix |= (ulong)(b << 4);
                                        break;

                                    case 6:
                                        thirtySix |= (ulong)(b << 12);
                                        break;

                                    case 7:
                                        thirtySix |= (ulong)(b << 20);
                                        break;

                                    case 8:
                                        thirtySix |= (ulong)(b << 28);

                                        // Second 36-bit word finished.
                                        WriteThirtySix(tapeOutput, MangleBits(thirtySix));
                                        wordCount18 += 2;

                                        byteIndex36 = 0;
                                        break;

                                    default:
                                        throw new InvalidOperationException("Unexpected state when building 36-bit word.");

                                }

                                //
                                // Deal with the 12-bit checksum
                                //
                                switch (byteIndex12++)
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
                                        byteIndex12 = 0;
                                        break;

                                    default:
                                        throw new InvalidOperationException("Unexpected state when building 12-bit checksum.");
                                }

                                //
                                // Start next block when at end.
                                //
                                if (wordCount18 == 256)
                                {
                                    readState = ReadState.BlockFlag;
                                    blockNumber++;
                                    byteIndex36 = 0;
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

        private static ulong MangleBits(ulong mangled)
        {
            ulong unmangled;

            //
            // Reading 18-bit DECTapes on a 12-bit PDP-8 presents an interesting 
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

        private static void WriteThirtySix(Stream outStream, ulong thirtySix)
        {
            WriteEighteen(outStream, (uint)(thirtySix & 0x3ffff));
            WriteEighteen(outStream, (uint)((thirtySix & 0xffffc0000) >> 18));
        }

        private static void WriteEighteen(Stream outStream, uint eighteen)
        {
            outStream.WriteByte((byte) (eighteen & 0x000000ff));
            outStream.WriteByte((byte)((eighteen & 0x0000ff00) >> 8));
            outStream.WriteByte((byte)((eighteen & 0x00ff0000) >> 16));
            outStream.WriteByte((byte)((eighteen & 0xff000000) >> 24));

            outStream.Flush();
        }

        public static string ToOctal(int i)
        {
            return Convert.ToString(i, 8);
        }
    }
}
