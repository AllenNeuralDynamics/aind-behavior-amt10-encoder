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
                                WriteTimeout = Timeout
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
                    
                    // Periodically request a counter reading
                    var readTimer = new System.Timers.Timer(500); // 500ms interval
                    readTimer.Elapsed += (s, e) =>
                    {
                        try
                        {
                            if (serialPort != null && serialPort.IsOpen)
                            {
                                // Send command 4 to read counter - no newline as in Python code
                                serialPort.Write("4");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error requesting counter value: {ex.Message}");
                        }
                    };
                    readTimer.Start();
                    
                    return Disposable.Create(() =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        
                        readTimer.Stop();
                        readTimer.Dispose();
                        
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
                // Turn debugger off - exactly like Python implementation
                Console.WriteLine("Turning off debugger");
                serialPort.Write("0"); // No newline, just like in Python
                bool success = WaitForResponse("OFF", 150);
                if (!success)
                {
                    Console.WriteLine("Failed to turn off debugger");
                    return false;
                }
                
                // Hard reset the counter first
                Console.WriteLine("Resetting counter chip");
                serialPort.Write("1"); // Reset command
                Thread.Sleep(100);
                
                // Read MDR0 and STR registers
                Console.WriteLine("Getting MDR0 register");
                ReadMDR0();
                
                Console.WriteLine("Getting STR register");
                ReadSTR();
                
                // Initialize MDR0 register - critical for quadrature counting
                Console.WriteLine("Initializing MDR0 register");
                serialPort.Write("8"); // Set MDR0 register
                bool mdrSuccess = WaitForResponse("MDR0", 150);
                if (!mdrSuccess)
                {
                    Console.WriteLine("Failed to initialize MDR0");
                    return false;
                }
                
                // Clear encoder
                Console.WriteLine("Clearing encoder counter");
                if (!ClearEncoder())
                {
                    Console.WriteLine("Failed to clear encoder");
                    return false;
                }
                
                // Get decoder version
                Console.WriteLine("Getting decoder version");
                serialPort.Write("5");
                bool versionSuccess = WaitForResponse("VERSION", 150);
                if (!versionSuccess)
                {
                    Console.WriteLine("Failed to get decoder version");
                    return false;
                }
                
                // Force read counter to start the counting process
                Console.WriteLine("Starting encoder read process");
                serialPort.Write("4");
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
            serialPort.Write("7"); // Read MDR0 command - no newline
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
            serialPort.Write("3"); // Read STR command - no newline
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
            serialPort.Write("2"); // Clear counter command - no newline like Python
            
            int attempts = 0;
            int maxAttempts = 3;
            
            while (attempts < maxAttempts)
            {
                try
                {
                    // First read after sending command
                    string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                    Console.WriteLine($"Clear response: {response}");
                    
                    // Extract count value from response if possible
                    Match match = Regex.Match(response, ";Count:(-?\\d+)");
                    if (match.Success)
                    {
                        int count = int.Parse(match.Groups[1].Value);
                        if (Math.Abs(count) < 100)
                        {
                            // Count is close to zero, consider cleared
                            return true;
                        }
                    }
                    
                    // If not cleared, try again
                    serialPort.Write("2");
                    attempts++;
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error clearing encoder: {ex.Message}");
                    attempts++;
                    Thread.Sleep(50);
                }
            }
            
            // Return true even if we couldn't confirm the clear, to allow continuing
            return true;
        }
        
        private void ReadEncoderData()
        {
            int noDataCount = 0;
            
            while (continueReading)
            {
                try
                {
                    if (serialPort != null && serialPort.IsOpen)
                    {
                        string line = serialPort.ReadLine().TrimEnd('\r', '\n');
                        
                        if (!string.IsNullOrEmpty(line))
                        {
                            noDataCount = 0;
                            
                            if (line.Contains(";Index:") && line.Contains(";Count:"))
                            {
                                // Valid encoder data format
                                currentValue = line;
                                Console.WriteLine($"Encoder data: {line}");
                            }
                        }
                        else
                        {
                            noDataCount++;
                            if (noDataCount > 10)
                            {
                                // Too many empty reads, request counter value
                                serialPort.Write("4");
                                noDataCount = 0;
                                Thread.Sleep(20);
                            }
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // Ignore timeout, but consider requesting data if we time out too often
                    noDataCount++;
                    if (noDataCount > 5)
                    {
                        try
                        {
                            serialPort.Write("4");
                            noDataCount = 0;
                        }
                        catch
                        {
                            // Ignore errors when requesting data
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error reading encoder: " + ex.Message);
                    Thread.Sleep(100);
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
                    Console.WriteLine($"Invalid data format: {data}");
                    return new AMT10EncoderReading
                    {
                        Index = lastIndex,
                        Count = lastCount,
                        Degrees = lastDegrees,
                        RawData = data
                    };
                }
                
                // Find the index and count parts (parts may be in different positions)
                string indexPart = null;
                string countPart = null;
                
                foreach (string part in parts)
                {
                    if (part.StartsWith("Index:"))
                    {
                        indexPart = part;
                    }
                    else if (part.StartsWith("Count:"))
                    {
                        countPart = part;
                    }
                }
                
                if (indexPart != null && countPart != null)
                {
                    int index = int.Parse(indexPart.Substring("Index:".Length));
                    int count = int.Parse(countPart.Substring("Count:".Length));
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
                Console.WriteLine($"Error parsing encoder data: {ex.Message}, Data: {data}");
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