"""
AMT10_quadrature_encoder.py - Interface to AMT10 encoder via Arduino with LS7366R chip

This module interfaces with an AMT10 quadrature encoder connected to an Arduino
that uses a LS7366R quadrature counter chip. It implements a protocol for
initializing the encoder, reading position data, and clearing the counter.

Original implementation from Allen Institute's camstim package.
"""

import logging
import serial
import threading
import time
from serial.serialutil import SerialException

class DigitalEncoder(object):
    """Interface to AMT10 encoder via Arduino with LS7366R chip."""
    
    def __init__(self, config=None, optional=None):
        """
        Initialize the encoder interface.
        
        Parameters
        ----------
        config : dict
            Configuration parameters for the encoder
        optional : dict
            Optional configuration parameters
            
        Returns
        -------
        None
        """
        if not config:
            config = {}
            
        # Get configuration parameters
        self.port = config.get('port', '/dev/ttyACM0')
        self.baudrate = config.get('baudrate', 9600)
        self.timeout = config.get('timeout', 0.5)
        self.debug = config.get('debug', False)
        
        # Initialize connection to Arduino
        try:
            self._encoder = serial.Serial(port=self.port,
                                         baudrate=self.baudrate,
                                         timeout=self.timeout)
            # Wait for Arduino to reset
            time.sleep(2)
        except SerialException as e:
            logging.error("Could not connect to encoder: {}".format(e))
            raise
            
        # Initialize encoder properties
        self._val = None
        self.mdr0 = None
        self.str_reg = None
        self.LS7366R_version = None
        self.running = True
        
        # Turn debugger on/off
        if self.debug:
            logging.info("Turning on debugger")
            self.turn_on_debugger()
        else:
            logging.info("Turning off debugger")
            self.turn_off_debugger()
            
        logging.info("Getting mode and status register")
        self.get_str_and_mdr0()
        
        logging.info("Initializing mdr0")
        self.initialize_MDR0()
        
        logging.info("Clearing encoder")
        self.clear_encoder()
        
        logging.info("Getting version")
        self.decoder_version()
        
        # Start background thread to continuously read encoder values
        self._thread = threading.Thread(target=self._read_encoder)
        self._thread.daemon = True
        self._thread.start()
    
    def _read_encoder(self):
        """Background thread to continuously read encoder values."""
        while self.running:
            try:
                data = self._encoder.readline().decode('utf-8').strip()
                if data:
                    self._val = data
            except Exception as e:
                logging.warning("Error reading encoder: {}".format(e))
                time.sleep(0.1)
    
    def position(self):
        """
        Get the current position of the encoder.
        
        Returns
        -------
        deg : float
            Current angle in degrees
        count : int
            Raw count value
        counter_index : int
            Index counter value
        raw_data : str
            Raw data string from the Arduino
        """
        if self._val is None:
            return None, None, None, None
        
        try:
            # Parse the data format: ;Index:123;Count:456
            parts = self._val.split(';')
            if len(parts) < 3:
                return None, None, None, self._val
                
            index_part = parts[1].split(':')
            count_part = parts[2].split(':')
            
            if len(index_part) == 2 and len(count_part) == 2:
                counter_index = int(index_part[1])
                count = int(count_part[1])
                deg = (count / 8192.0) * 360.0  # Convert counts to degrees
                return deg, count, counter_index, self._val
            else:
                return None, None, None, self._val
        except Exception as e:
            logging.warning("Error parsing encoder data: {}".format(e))
            return None, None, None, self._val
    
    def turn_off_debugger(self):
        """Turn off debug output from Arduino."""
        self._encoder.write("0".encode())
        count = 0
        found = False
        while count < 150 and not found:
            resp = self._encoder.readline()
            if b"OFF" in resp:
                found = True
            count += 1
        return found
    
    def turn_on_debugger(self):
        """Turn on debug output from Arduino."""
        self._encoder.write("9".encode())
        count = 0
        found = False
        while count < 150 and not found:
            resp = self._encoder.readline()
            if b"ON" in resp:
                found = True
            count += 1
        return found
    
    def read_MDR0(self):
        """Read the MDR0 register value from LS7366R chip."""
        self._encoder.write("7".encode())
        count = 0
        while count < 150:
            resp = self._encoder.readline()
            if b"MDR0" in resp:
                num_str = resp.decode('utf-8').strip().split(":")
                if len(num_str) > 1:
                    self.mdr0 = int(num_str[1])
                    return self.mdr0
            count += 1
        logging.warning("Could not read MDR0")
        return None
    
    def read_STR(self):
        """Read the STR register value from LS7366R chip."""
        self._encoder.write("3".encode())
        count = 0
        while count < 150:
            resp = self._encoder.readline()
            if b"STR" in resp:
                num_str = resp.decode('utf-8').strip().split(":")
                if len(num_str) > 1:
                    self.str_reg = int(num_str[1])
                    return self.str_reg
            count += 1
        logging.warning("Could not read STR")
        return None
    
    def get_str_and_mdr0(self):
        """Get both MDR0 and STR register values."""
        self.read_MDR0()
        self.read_STR()
        return self.mdr0, self.str_reg
    
    def initialize_MDR0(self):
        """Initialize the MDR0 register with proper settings."""
        self._encoder.write("8".encode())
        count = 0
        while count < 150:
            resp = self._encoder.readline()
            if b"MDR0" in resp:
                num_str = resp.decode('utf-8').strip().split(":")
                if len(num_str) > 1:
                    self.mdr0 = int(num_str[1])
                    return self.mdr0
            count += 1
        logging.warning("Could not initialize MDR0")
        return None
    
    def clear_encoder(self):
        """Clear the encoder counter to reset the position."""
        # 2 must be sent to the arduino to recognize the clear
        self._encoder.write("2".encode())
        count = 0
        register_val = self._encoder.readline()[:-2]
        try:
            c = int(register_val.split(";")[-1].split(":")[-1])
            while c > 1000 or c < -1000:
                count += 1
                if count == 150:
                    logging.warning("Could not clear CTR register")
                    break
                rval = self._encoder.readline()[:-2]
                c = int(rval.split(";")[-1].split(":")[-1])
        except:
            logging.warning("Error clearing encoder")
            return False
        return True
    
    def decoder_version(self):
        """Get the version of the LS7366R decoder firmware."""
        self._encoder.write("5".encode())
        count = 0
        while count < 150:
            resp = self._encoder.readline()
            if b"VERSION" in resp:
                num_str = resp.decode('utf-8').strip().split(":")
                if len(num_str) > 1:
                    self.LS7366R_version = num_str[1]
                    return self.LS7366R_version
            count += 1
        logging.warning("Could not get encoder version")
        return None
    
    def close(self):
        """Close the connection to the encoder."""
        self.running = False
        if hasattr(self, '_thread'):
            self._thread.join(timeout=1.0)
        if hasattr(self, '_encoder') and self._encoder.is_open:
            self._encoder.close()