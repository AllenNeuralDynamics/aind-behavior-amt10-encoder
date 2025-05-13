using System;
using System.ComponentModel;
using System.IO.Ports;
using System.Reactive.Linq;
using System.Reactive.Disposables;  // Added for Disposable class
using System.Text.RegularExpressions;
using System.Threading;
using Bonsai; // Correct namespace for Source<> class

namespace Aind.Behavior.Amt10Encoder
{
    /// <summary>
    /// Represents a source of AMT10 quadrature encoder readings accessed through an Arduino.
    /// </summary>
    [Description("Produces a sequence of position readings from an AMT10 quadrature encoder via Arduino.")]
    public class AMT10EncoderSource : Source<AMT10EncoderReading>
    {
        private SerialPort serialPort;
        private Thread readingThread;
        private volatile bool continueReading;
        private volatile string currentValue;
        private readonly object lockObject = new object();
        private int errorCount = 0;
        private int lastIndex = 0;
        private int lastCount = 0;
        private double lastDegrees = 0;
        private string lastRawData = "";
        
        /// <summary>
        /// Gets or sets the name of the serial port.
        /// </summary>
        [Description("The name of the serial port connected to the Arduino.")]
        public string PortName { get; set; }
        
        /// <summary>
        /// Gets or sets the baud rate for the serial port.
        /// </summary>
        [Description("The baud rate of the serial port (default is 9600).")]
        public int BaudRate { get; set; } = 9600;
        
        /// <summary>
        /// Gets or sets the timeout for serial communication in milliseconds.
        /// </summary>
        [Description("The timeout for serial communication in milliseconds.")]
        public int Timeout { get; set; } = 500;
        
        /// <summary>
        /// Gets or sets the counts per revolution of the encoder.
        /// </summary>
        [Description("The counts per revolution of the encoder (default is 8192 for AMT10 encoder).")]
        public double CountsPerRevolution { get; set; } = 8192.0;
        
        /// <summary>
        /// Gets or sets a value indicating whether debug output from the Arduino is enabled.
        /// </summary>
        [Description("Enable debug output from the Arduino.")]
        public bool Debug { get; set; } = false;

        /// <summary>
        /// Generates an observable sequence of encoder readings.
        /// </summary>
        /// <returns>An observable sequence of encoder readings.</returns>
        public override IObservable<AMT10EncoderReading> Generate()
        {
            return Observable.Create<AMT10EncoderReading>(observer =>
            {
                try
                {
                    // Initialize and open serial port
                    lock (lockObject)
                    {
                        if (serialPort == null)
                        {
                            serialPort = new SerialPort(PortName, BaudRate)
                            {
                                DtrEnable = true,
                                RtsEnable = true,
                                ReadTimeout = Timeout,
                                WriteTimeout = Timeout,
                                NewLine = "\n"
                            };
                            serialPort.Open();
                            
                            // Wait for Arduino to reset - important for reliable communication
                            Thread.Sleep(2000);
                        }
                    }
                    
                    // Initialize encoder
                    bool initialized = InitializeEncoder();
                    if (!initialized)
                    {
                        observer.OnError(new Exception("Failed to initialize AMT10 encoder."));
                        return Disposable.Empty;
                    }
                    
                    // Start background reading thread
                    continueReading = true;
                    readingThread = new Thread(ReadEncoderData);
                    readingThread.IsBackground = true;
                    readingThread.Start();
                    
                    // Start timer to emit readings
                    var timer = new System.Timers.Timer(10); // 10ms interval
                    timer.Elapsed += (s, e) =>
                    {
                        try
                        {
                            var reading = ParseEncoderData();
                            if (reading != null)
                            {
                                observer.OnNext(reading);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error processing encoder data: {ex.Message}");
                        }
                    };
                    timer.Start();
                    
                    return Disposable.Create(() =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        
                        continueReading = false;
                        if (readingThread != null && readingThread.IsAlive)
                        {
                            readingThread.Join(500);
                        }
                        
                        CloseSerialPort();
                    });
                }
                catch (Exception ex)
                {
                    observer.OnError(ex);
                    return Disposable.Empty;
                }
            });
        }
        
        private bool InitializeEncoder()
        {
            try
            {
                // Turn debugger on/off
                if (Debug)
                {
                    Console.WriteLine("Turning on debugger");
                    serialPort.Write("9\n");
                    bool success = WaitForResponse("ON", 150);
                    if (!success)
                    {
                        Console.WriteLine("Failed to turn on debugger");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine("Turning off debugger");
                    serialPort.Write("0\n");
                    bool success = WaitForResponse("OFF", 150);
                    if (!success)
                    {
                        Console.WriteLine("Failed to turn off debugger");
                        return false;
                    }
                }

                // First, explicitly reset the counter chip fully
                Console.WriteLine("Hard resetting LS7366R counter");
                serialPort.Write("1\n"); // Command 1: Reset the LS7366R chip
                Thread.Sleep(100);
                
                // Read MDR0 and STR registers to check status
                Console.WriteLine("Getting mode and status registers");
                ReadMDR0();
                ReadSTR();
                
                // Initialize MDR0 register with proper settings
                Console.WriteLine("Initializing MDR0");
                serialPort.Write("8\n");
                bool mdrSuccess = WaitForResponse("MDR0", 150);
                if (!mdrSuccess)
                {
                    Console.WriteLine("Failed to initialize MDR0");
                    return false;
                }

                // Double check MDR0 is properly set
                int? mdr0 = ReadMDR0();
                Console.WriteLine($"MDR0 after initialization: {mdr0}");
                
                // Clear encoder multiple times to ensure zero position
                Console.WriteLine("Clearing encoder");
                if (!ClearEncoder())
                {
                    Console.WriteLine("Failed to clear encoder");
                    return false;
                }
                
                // Force read the counter to initialize counter reading
                Console.WriteLine("Initializing counter reading");
                serialPort.Write("4\n"); // Command 4: Read counter value
                Thread.Sleep(200);
                DrainSerialBuffer(); // Clear out any pending messages
                
                // Force another counter reset to ensure zero starting point
                serialPort.Write("2\n");
                Thread.Sleep(100);
                
                // Get decoder version
                Console.WriteLine("Getting decoder version");
                serialPort.Write("5\n");
                bool versionSuccess = WaitForResponse("VERSION", 150);
                if (!versionSuccess)
                {
                    Console.WriteLine("Failed to get decoder version");
                    return false;
                }

                // One more counter read to verify encoder is working
                serialPort.Write("4\n");
                Thread.Sleep(100);
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error initializing encoder: " + ex.Message);
                return false;
            }
        }
        
        private bool WaitForResponse(string expectedResponse, int maxTries)
        {
            int count = 0;
            while (count < maxTries)
            {
                try
                {
                    string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                    Console.WriteLine($"Response: {response}");
                    
                    if (response.Contains(expectedResponse))
                    {
                        return true;
                    }
                    count++;
                }
                catch (TimeoutException)
                {
                    count++;
                }
            }
            
            return false;
        }
        
        private int? ReadMDR0()
        {
            serialPort.Write("7\n");
            int count = 0;
            while (count < 150)
            {
                try
                {
                    string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                    Console.WriteLine($"Response: {response}");
                    
                    if (response.Contains("MDR0"))
                    {
                        var parts = response.Split(':');
                        if (parts.Length > 1)
                        {
                            return int.Parse(parts[1]);
                        }
                    }
                    count++;
                }
                catch (TimeoutException)
                {
                    count++;
                }
            }
            
            Console.WriteLine("Could not read MDR0");
            return null;
        }
        
        private int? ReadSTR()
        {
            serialPort.Write("3\n");
            int count = 0;
            while (count < 150)
            {
                try
                {
                    string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                    Console.WriteLine($"Response: {response}");
                    
                    if (response.Contains("STR"))
                    {
                        var parts = response.Split(':');
                        if (parts.Length > 1)
                        {
                            return int.Parse(parts[1]);
                        }
                    }
                    count++;
                }
                catch (TimeoutException)
                {
                    count++;
                }
            }
            
            Console.WriteLine("Could not read STR");
            return null;
        }
        
        private bool ClearEncoder()
        {
            serialPort.Write("2\n"); // Send clear encoder command
            
            int maxAttempts = 5;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    // First read after sending command
                    string registerVal = serialPort.ReadLine().TrimEnd('\r', '\n');
                    Console.WriteLine($"Clear response: {registerVal}");
                    
                    // Extract count value from response
                    Match match = Regex.Match(registerVal, ";Count:(-?\\d+)");
                    if (match.Success)
                    {
                        int countValue = int.Parse(match.Groups[1].Value);
                        
                        // If count is close to zero, success
                        if (Math.Abs(countValue) < 10)
                        {
                            return true;
                        }
                    }
                    
                    // Try again if not successful
                    Console.WriteLine("Retrying encoder clear...");
                    serialPort.Write("2\n");
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error on clear attempt {attempt}: {ex.Message}");
                }
            }
            
            Console.WriteLine("Warning: Could not confirm encoder clear after multiple attempts");
            return true; // Continue despite warning, might still work
        }
        
        private void DrainSerialBuffer()
        {
            try
            {
                // Read and discard any data sitting in the buffer
                while (serialPort.BytesToRead > 0)
                {
                    string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                    Console.WriteLine($"Draining: {response}");
                }
            }
            catch (TimeoutException)
            {
                // Ignore timeout exceptions during buffer drain
            }
        }
        
        private void ReadEncoderData()
        {
            int emptyLineCount = 0;
            
            while (continueReading)
            {
                try
                {
                    if (serialPort != null && serialPort.IsOpen)
                    {
                        // Check if we need to request a new reading
                        if (emptyLineCount > 5)
                        {
                            // Too many empty reads, request a value explicitly
                            serialPort.Write("4\n"); // Command to read counter
                            emptyLineCount = 0;
                        }
                        
                        string line = serialPort.ReadLine().TrimEnd('\r', '\n');
                        if (!string.IsNullOrEmpty(line))
                        {
                            emptyLineCount = 0;
                            
                            if (line.Contains("ERROR"))
                            {
                                errorCount++;
                                if (errorCount >= 5)
                                {
                                    Console.WriteLine("Too many encoder errors: " + line);
                                    continueReading = false;
                                }
                            }
                            else if (line.Contains(";Index:") && line.Contains(";Count:"))
                            {
                                // Only update current value if it contains both Index and Count
                                currentValue = line;
                                
                                // Debug output to console periodically
                                if (Debug && DateTime.Now.Second % 5 == 0)
                                {
                                    Console.WriteLine($"Current encoder data: {line}");
                                }
                            }
                        }
                        else
                        {
                            emptyLineCount++;
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // Ignore timeout and continue
                    emptyLineCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error reading encoder: " + ex.Message);
                    Thread.Sleep(100);
                    emptyLineCount++;
                }
            }
        }
        
        private AMT10EncoderReading ParseEncoderData()
        {
            string data = currentValue;
            if (string.IsNullOrEmpty(data))
            {
                return new AMT10EncoderReading
                {
                    Index = lastIndex,
                    Count = lastCount,
                    Degrees = lastDegrees,
                    RawData = lastRawData
                };
            }
            
            try
            {
                // Parse the data format: ";Index:123;Count:456"
                string[] parts = data.Split(';');
                if (parts.Length < 3)
                {
                    return new AMT10EncoderReading
                    {
                        Index = lastIndex,
                        Count = lastCount,
                        Degrees = lastDegrees,
                        RawData = data
                    };
                }
                
                string[] indexPart = parts[1].Split(':');
                string[] countPart = parts[2].Split(':');
                
                if (indexPart.Length == 2 && countPart.Length == 2)
                {
                    int index = int.Parse(indexPart[1]);
                    int count = int.Parse(countPart[1]);
                    double degrees = (count / CountsPerRevolution) * 360.0;
                    
                    // Store values for future use if there's an error
                    lastIndex = index;
                    lastCount = count;
                    lastDegrees = degrees;
                    lastRawData = data;
                    
                    return new AMT10EncoderReading
                    {
                        Index = index,
                        Count = count,
                        Degrees = degrees,
                        RawData = data
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error parsing encoder data: " + ex.Message);
            }
            
            // Return last valid reading on error
            return new AMT10EncoderReading
            {
                Index = lastIndex,
                Count = lastCount,
                Degrees = lastDegrees,
                RawData = lastRawData
            };
        }
        
        private void CloseSerialPort()
        {
            lock (lockObject)
            {
                if (serialPort != null && serialPort.IsOpen)
                {
                    try
                    {
                        serialPort.Close();
                        serialPort.Dispose();
                        serialPort = null;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error closing serial port: " + ex.Message);
                    }
                }
            }
        }
    }
}