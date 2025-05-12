using System;
using System.ComponentModel;
using System.IO.Ports;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bonsai.AMT10
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
                    if (!SendCommandAndWaitForResponse("9", "ON", 150))
                    {
                        Console.WriteLine("Failed to turn on debugger");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine("Turning off debugger");
                    if (!SendCommandAndWaitForResponse("0", "OFF", 150))
                    {
                        Console.WriteLine("Failed to turn off debugger");
                        return false;
                    }
                }
                
                // Read MDR0 and STR
                Console.WriteLine("Getting mode and status registers");
                if (!SendCommandAndWaitForResponse("7", "MDR0", 150))
                {
                    Console.WriteLine("Failed to read MDR0");
                    return false;
                }
                
                if (!SendCommandAndWaitForResponse("3", "STR", 150))
                {
                    Console.WriteLine("Failed to read STR");
                    return false;
                }
                
                // Initialize MDR0
                Console.WriteLine("Initializing MDR0");
                if (!SendCommandAndWaitForResponse("8", "MDR0", 150))
                {
                    Console.WriteLine("Failed to initialize MDR0");
                    return false;
                }
                
                // Clear encoder
                Console.WriteLine("Clearing encoder");
                if (!ClearEncoder())
                {
                    Console.WriteLine("Failed to clear encoder");
                    return false;
                }
                
                // Get decoder version
                Console.WriteLine("Getting decoder version");
                if (!SendCommandAndWaitForResponse("5", "VERSION", 150))
                {
                    Console.WriteLine("Failed to get decoder version");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error initializing encoder: " + ex.Message);
                return false;
            }
        }
        
        private bool SendCommandAndWaitForResponse(string command, string expectedResponse, int maxTries)
        {
            serialPort.WriteLine(command);
            
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
        
        private bool ClearEncoder()
        {
            serialPort.WriteLine("2");
            
            int count = 0;
            try
            {
                string registerVal = serialPort.ReadLine().TrimEnd('\r', '\n');
                
                // Extract count value from response
                Match match = Regex.Match(registerVal, ";Count:(-?\\d+)");
                if (!match.Success)
                {
                    return false;
                }
                
                int countValue = int.Parse(match.Groups[1].Value);
                
                // Wait until count is close to zero
                while ((countValue > 1000 || countValue < -1000) && count < 150)
                {
                    string rval = serialPort.ReadLine().TrimEnd('\r', '\n');
                    Match newMatch = Regex.Match(rval, ";Count:(-?\\d+)");
                    if (newMatch.Success)
                    {
                        countValue = int.Parse(newMatch.Groups[1].Value);
                    }
                    count++;
                }
                
                return count < 150;
            }
            catch (Exception)
            {
                return false;
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
                        string line = serialPort.ReadLine().TrimEnd('\r', '\n');
                        if (!string.IsNullOrEmpty(line))
                        {
                            if (line.Contains("ERROR"))
                            {
                                errorCount++;
                                if (errorCount >= 5)
                                {
                                    Console.WriteLine("Too many encoder errors: " + line);
                                    continueReading = false;
                                }
                            }
                            else
                            {
                                currentValue = line;
                            }
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // Ignore timeout and continue
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
                var indexMatch = Regex.Match(data, ";Index:(\\d+);");
                var countMatch = Regex.Match(data, ";Count:(-?\\d+)");
                
                if (indexMatch.Success && countMatch.Success)
                {
                    int index = int.Parse(indexMatch.Groups[1].Value);
                    int count = int.Parse(countMatch.Groups[1].Value);
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