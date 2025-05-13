using System;
using System.ComponentModel;
using System.IO.Ports;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Bonsai;

namespace Aind.Behavior.Amt10Encoder
{
    /// <summary>
    /// Provides an operator that resets an AMT10 quadrature encoder counter to zero.
    /// </summary>
    [Description("Sends a command to reset an AMT10 quadrature encoder counter to zero.")]
    public class AMT10ResetEncoder : Sink<object>
    {
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
        /// Sends the reset command to the encoder whenever the observable sequence emits a notification.
        /// </summary>
        /// <typeparam name="TSource">The type of the elements in the source sequence.</typeparam>
        /// <param name="source">The sequence of notifications used to trigger the reset command.</param>
        /// <returns>The source sequence.</returns>
        public override IObservable<object> Process(IObservable<object> source)
        {
            return source.Do(input =>
            {
                try
                {
                    using (var serialPort = new SerialPort(PortName, BaudRate)
                    {
                        DtrEnable = true,
                        RtsEnable = true,
                        ReadTimeout = Timeout,
                        WriteTimeout = Timeout
                    })
                    {
                        serialPort.Open();
                        
                        // Wait for Arduino to initialize
                        System.Threading.Thread.Sleep(100);
                        
                        // Step 1: First reset the LS7366R chip
                        Console.WriteLine("Resetting LS7366R chip");
                        serialPort.Write("1");  // Send reset command without newline
                        System.Threading.Thread.Sleep(100);
                        
                        // Read and discard any pending messages
                        while (serialPort.BytesToRead > 0)
                        {
                            string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                            Console.WriteLine($"Reset response: {response}");
                        }
                        
                        // Step 2: Clear encoder counter multiple times to ensure it's zeroed
                        Console.WriteLine("Clearing encoder counter");
                        for (int i = 0; i < 3; i++)
                        {
                            serialPort.Write("2");  // Send clear command without newline
                            System.Threading.Thread.Sleep(50);
                            
                            bool success = false;
                            int attempts = 0;
                            while (attempts < 10 && !success)
                            {
                                try
                                {
                                    string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                                    Console.WriteLine($"Clear response: {response}");
                                    
                                    // Check for expected response format
                                    Match match = Regex.Match(response, ";Count:(-?\\d+)");
                                    if (match.Success)
                                    {
                                        int count = int.Parse(match.Groups[1].Value);
                                        if (Math.Abs(count) < 100)
                                        {
                                            success = true;
                                            Console.WriteLine($"Encoder successfully cleared. Count: {count}");
                                        }
                                    }
                                }
                                catch (TimeoutException)
                                {
                                    // Continue trying on timeout
                                }
                                
                                attempts++;
                            }
                            
                            if (success) break;
                        }
                        
                        // Step 3: Force read the counter to verify it's cleared
                        serialPort.Write("4");
                        System.Threading.Thread.Sleep(50);
                        
                        try
                        {
                            string finalReading = serialPort.ReadLine().TrimEnd('\r', '\n');
                            Console.WriteLine($"Final reading after reset: {finalReading}");
                        }
                        catch (TimeoutException)
                        {
                            Console.WriteLine("No response from counter after reset");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error resetting encoder: {ex.Message}");
                }
            });
        }
    }
}