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
                            // This matches Python's time.sleep(2) before initialization
                            Console.WriteLine("Waiting for Arduino to initialize...");
                            Thread.Sleep(1000);
                        }
                    }
                    
                    // Initialize encoder - BEFORE starting the reading thread
                    // This matches Python's initialization sequence
                    bool initialized = InitializeEncoder();
                    if (!initialized)
                    {
                        observer.OnError(new Exception("Failed to initialize AMT10 encoder."));
                        return Disposable.Empty;
                    }
                    
                    // Add a short delay after initialization to ensure the Arduino is ready
                    // This gives the Arduino time to settle after all initialization commands
                    Console.WriteLine("Waiting for Arduino to stabilize after initialization...");
                    Thread.Sleep(1000); // Increased from 500ms to 1000ms for better reliability
                    
                    // Only AFTER initialization succeeds, start the background reading thread
                    // This matches Python's thread start sequence
                    continueReading = true;
                    readingThread = new Thread(ReadEncoderData);
                    readingThread.IsBackground = true;
                    readingThread.Start();
                    
                    // Start timer to emit readings (this emits values based on what the read thread collects)
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
                // Turn debugger off - exactly like Python implementation
                Console.WriteLine("Turning off debugger");
                lock (lockObject)
                {
                    serialPort.Write("0"); // No newline, exactly like Python's "0".encode()
                    bool success = WaitForResponse("OFF", 150);
                    if (!success)
                    {
                        Console.WriteLine("Failed to turn off debugger");
                        return false;
                    }
                }
                
                // Get mode and status registers - this comes BEFORE initializing MDR0 in Python
                Console.WriteLine("Getting mode and status registers");
                var mdr0 = ReadMDR0();
                var str = ReadSTR();
                Console.WriteLine($"Initial MDR0: {mdr0}, STR: {str}");
                
                // Initialize MDR0 register - critical for quadrature counting
                Console.WriteLine("Initializing MDR0 register");
                lock (lockObject)
                {
                    serialPort.Write("8"); // Set MDR0 register - like Python
                    bool mdrSuccess = WaitForResponse("MDR0", 150);
                    if (!mdrSuccess)
                    {
                        Console.WriteLine("Failed to initialize MDR0");
                        return false;
                    }
                }
                
                // Clear encoder - Python does this AFTER setting MDR0
                Console.WriteLine("Clearing encoder counter");
                lock (lockObject)
                {
                    serialPort.Write("2"); // Clear counter command
                    Thread.Sleep(100);
                    
                    // Read response and verify count is close to zero like Python does
                    try
                    {
                        string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                        Console.WriteLine($"Clear response: {response}");
                        
                        // Extract count value to make sure it's close to zero
                        Match match = Regex.Match(response, ";Count:(-?\\d+)");
                        if (match.Success)
                        {
                            int count = int.Parse(match.Groups[1].Value);
                            if (Math.Abs(count) > 1000)
                            {
                                Console.WriteLine($"Warning: Count not close to zero after clear: {count}");
                            }
                        }
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine("No response to clear command");
                    }
                }
                
                // Get decoder version
                Console.WriteLine("Getting decoder version");
                lock (lockObject)
                {
                    serialPort.Write("5");
                    bool versionSuccess = WaitForResponse("VERSION", 150);
                    if (!versionSuccess)
                    {
                        Console.WriteLine("Failed to get decoder version");
                        return false;
                    }
                }
                
                // Drain any data in the buffer after Arduino reset
                DrainSerialBuffer();
                
                // Python does NOT send command "4" during initialization
                // The Arduino firmware automatically sends encoder data
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error initializing encoder: " + ex.Message);
                return false;
            }
        }
        
        private void DrainSerialBuffer()
        {
            try
            {
                lock (lockObject) // Added lock for thread safety
                {
                    // Read and discard any data sitting in the buffer
                    // This is critical for reliable command handling and avoiding command/response desync
                    while (serialPort.BytesToRead > 0)
                    {
                        string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                        Console.WriteLine($"Draining: {response}");
                    }
                }
            }
            catch (TimeoutException)
            {
                // Ignore timeout exceptions during buffer drain
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
            lock (lockObject)
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
        }
        
        private int? ReadSTR()
        {
            lock (lockObject)
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
        }
        
        private void ReadEncoderData()
        {
            while (continueReading)
            {
                try
                {
                    if (serialPort != null && serialPort.IsOpen)
                    {
                        lock (lockObject)
                        {
                            string line = serialPort.ReadLine().TrimEnd('\r', '\n');
                            
                            if (!string.IsNullOrEmpty(line))
                            {
                                // Store any non-empty data like Python does
                                currentValue = line;
                                
                                if (Debug && line.Contains(";Count:"))
                                {
                                    Console.WriteLine($"Encoder data: {line}");
                                }
                            }
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // Just ignore timeout exceptions, like Python does
                    Thread.Sleep(10);
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