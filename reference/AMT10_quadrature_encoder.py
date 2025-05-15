from datetime import datetime as dt
import logging
import os
import serial
import socket
from threading import Thread
import time

"""
    Wrapper for Arduino microcontroller which reads from the LS3677R quadrature counter chip

    AMT10 Series Quadrature Encoder Specs:
        encoder provides 2048 pulses per revolution
        decoder chip (LS3677R) outputs 8192 counts per revolution of the quadrature encoder (AMT10 Series)
"""


COUNTS_PER_REV = 8192.0

class BaseEncoder(object):
    def __init__(self):
        pass 

    def initialize(self):
        pass 

    def open(self):
        pass

    def close(self):
        pass

class DigitalEncoder(BaseEncoder):
    def __init__(self,config={}, dummy=False, debug=False, optional=None):
        super(DigitalEncoder,self).__init__()
        self.dummy = dummy
        self.comport = config.get("serial_device", None) or "COM3"
        self.baudrate = config.get("baudrate", None) or 9600
        self.timeout = config.get("timeout", None) or 0.5
        self.counts_per_rev = config.get("counts", None) or COUNTS_PER_REV
        logging.info(config)
        

        self._encoder_count= None
        self._encoder = None
        self._continue_reading = True
        self._last_deg = 0
        self._last_count = 0
        self._count = 0
        self._last_index = 0
        self._last_data = None
        self.deg = 0
        self._val = None
        self.index = None
        self.raw_data = None
        self.post_init_mdr0 = None
        self.mdr0 = None
        self.LS7366R_version = None
        self.bad_reads = 0
        self._tracking = 0
        self.error_count = 0

        try:
            self.initialize()
        except serial.serialutil.SerialException:
            logging.critical("Could not connect to the Arduino over USB")
            exit(-6)
    
        self.byte_list = []
        time.sleep(1.0)

        if debug:
            logging.info("Turning on debugger")
            self.turn_on_debugger()
        else:
            logging.info("Turning off debugger")
            self.turn_off_debugger()
        logging.info("Getting mode and status register")
        self.get_str_and_mdr0() # log the current STR and MDR0 values before initializing MDR0
        logging.info("initializing mdr0")
        self.initialize_MDR0()
        logging.info("clearing encoder")
        self.clear_encoder()
        logging.info("getting version")
        self.decoder_version()
        logging.info("Read_status, {STR: %s, MDR0: %s}, Write_status, {MDR0: %s}" % (self.str_val, self.post_init_mdr0, self.mdr0), extra = {"weblog": True})
        logging.info("Arduino_version, {}".format(self.LS7366R_version), extra = {"weblog": True})

        self.begin_reading = Thread(target=self._read_encoder)
        self.begin_reading.setDaemon(True)
        self.begin_reading.start()
        

        
    def initialize(self):
        if not self.dummy:
            self._encoder = serial.Serial(port=self.comport, baudrate=self.baudrate, timeout=self.timeout)
        else:
            self._encoder = DummyEncoder()


    def position(self):
        #storing count as val so that count does not update through this process
        decoder_data = self._val
        try:
            if "ERROR" in decoder_data:
                return self._last_deg, self._last_count, self._last_index, self._last_data
            # check string off of serial port is one index and one count value
            try:
                if len(decoder_data.split(";")) > 3:
                    logging.error("We are not sampling correctly. Multiple encoder data found in one sample, shutting down")
                    exit(-30)
                # data in format: ";Index:123;Count:124334"
            except AttributeError:
                self._tracking += 1
                return self._last_deg, self._last_count, self._last_index, self._last_data
                if self._tracking == 5:
                    logging.error("Multiple none values read, exiting, {}".format(self._tracking))
                    raise AttributeError
            try:
                self.index = int(decoder_data.split(";")[-2].split(":")[-1])
                self._count = int(decoder_data.split(";")[-1].split(":")[-1])
                self.raw_data = decoder_data
            except IndexError:
                self.bad_reads =+ 1
                if self.bad_reads == 10:
                    logging.error("Data is not correct type, {}, shutting down".format(decoder_data))
                    exit(-1)
                return self._last_deg, self._last_count, self._last_index, self._last_data
            except ValueError:
                return self._last_deg, self._last_count, self._last_index,  self._last_data
        except TypeError:
            pass

        # This will check to see if multiple values are printed on the serial port. E.g. ";Index:123;Count:124334";Index:124;Count:130090"
        try:
            self.deg = (self._count/self.counts_per_rev) * 360.0
            self._last_deg = self.deg
            self._last_count = self._count
            self._last_index = self.index
            self._last_data = self.raw_data
            return self.deg, self._count, self.index, self.raw_data
            # I don't care so much if there is a type error. There have been cases where the encoder throws garbage
        except TypeError:
            return self._last_deg, self._last_count, self._last_index, self._last_data
        # there is a reason that I raise an attribute error but I don't remember...I am not removing it
        except AttributeError:
            raise

    def clear_encoder(self):
        # 2 must be sent to the arduino to recognize the clear
        self._encoder.write("2")
        count = 0
        register_val = self._encoder.readline()[:-2]
        c = int(register_val.split(";")[-1].split(":")[-1])
        while c > 1000 or c < -1000 :
            count += 1
            if count == 150:
                logging.warning("Could not clear CTR register")
                break
            rval = self._encoder.readline()[:-2]
            c = int(rval.split(";")[-1].split(":")[-1])
            pass
    
    def turn_on_debugger(self):
        self._encoder.write("9")
        count = 0
        while True:
            register_val = self._encoder.readline()[:-2]
            count += 1
            if count == 150:
                logging.error("Could not turn on debugger")
                self.abort()
            try:
                if "CMD" in register_val:
                    logging.info("Comand, {}".format(register_val))
                if "ON" in register_val:
                    logging.info("Debugger turned on")
                    break
            except TypeError:
                continue
    
    def turn_off_debugger(self):
        self._encoder.write("0")
        count = 0
        while True:
            register_val = self._encoder.readline()[:-2]
            count += 1
            if count == 150:
                logging.error("Could not turn off debugger")
                self.abort()
            try:
                if "CMD" in register_val:
                    logging.info("Comand, {}".format(register_val))
                if "OFF" in register_val:
                    logging.info("Debugger turned off")
                    break
            except TypeError:
                continue

    def get_str_and_mdr0(self):
        self.read_MDR0()
        self.read_STR()

    def decoder_version(self):
        self._encoder.write("5")
        count = 0
        while True:
            register_val = self._encoder.readline()[:-2]
            count += 1
            if count == 150:
                logging.error("Could not obtain version")
                self.abort()
            try:
                if "CMD" in register_val:
                    logging.info("Comand, {}".format(register_val))
                if "VERSION" in register_val:
                    self.LS7366R_version = register_val.split(":")[-1]
                    break
            except TypeError:
                continue
                    
    def read_MDR0(self):
        self._encoder.write("7")
        count = 0
        while True:
            register_val = self._encoder.readline()[:-2]
            count += 1
            if count == 150:
                logging.error("Could not obtain mode register value")
                try: # depends on where the process is (has threading started)
                    self.close()
                except:
                    self.abort()
            try:
                if "CMD" in register_val:
                    logging.info("Comand, {}".format(register_val))
                if "MDR0" in register_val:
                    try:
                        self.post_init_mdr0 = int(register_val.split(":")[-1])
                    except:
                        logging.error("Could not obtain MDR0 register value")
                        try: # depends on where the process is (has threading started)
                            self.close()
                        except:
                            self.abort()
                    break
            except TypeError:
                continue
    
    def read_STR(self):
        self._encoder.write("3")
        count = 0
        while True:
            register_val = self._encoder.readline()[:-2]
            count += 1
            if count == 150:
                logging.error("Could not obtain status register value")
                try: # depends on where the process is (has threading started)
                    self.close()
                except:
                    self.abort()
            try: 
                if "CMD" in register_val:
                    logging.info("Comand, {}".format(register_val))
                if "STR" in register_val:
                    try:
                        self.str_val = int(register_val.split(":")[-1])
                        # power loss bit on 2**2
                        if (self.str_val & 4) == 0: 
                            logging.info("Power loss bit low",extra={'weblog':True})
                    except:
                        logging.error("Could not obtain Status register value")
                        try: # depends on where the process is (has threading started)
                            self.close()
                        except:
                            self.abort()
                    break
            except TypeError:
                continue

    def initialize_MDR0(self):
        self._encoder.write("8")
        count = 0
        while True:
            register_val = self._encoder.readline()[:-2]
            count += 1
            if count == 150:
                logging.error("MDR0 was not set")
                self.abort()
            try:
                if "CMD" in register_val:
                    logging.info("Comand, {}".format(register_val))
                if "MDR0" in register_val:
                    mdr0 = register_val.split(":")[-1]
                    if int(mdr0) != 3:
                        logging.error("MDR0 was not configured correctly")
                        self.abort()
                    try:
                        self.mdr0 = mdr0
                    except:
                        logging.error("Could not obtain MDR0 register value")
                        self.abort()
                    break
            except TypeError:
                continue

    def _read_encoder(self):
        while self._continue_reading:
            self._val = self._encoder.readline()[:-2]
            if not self._val:
                self._val = None
            if "ERROR" in self._val:
                self.error_count +=1
                if self.error_count == 5:
                    logging.error("Error detected, {}".format(self._val))
                    self._continue_reading = False
                    
    def open(self):
        self._encoder.open()

    def abort(self):
        self._encoder.close()
        exit(-30)

    def close(self):
        # camstim can close
        self._continue_reading = False
        self._encoder.close()
        try:
            self.begin_reading.join()
            self.get_str_and_mdr0()
            logging.info("Read_status, {STR: %s, MDR0: %s}" % (self.str_val, self.post_init_mdr0), extra = {"weblog": True})
        except Exception as e:
            print(e)

class DummyEncoder(BaseEncoder):
    def __init__(self):
        super(DummyEncoder,self).__init__()
        self.connect_dummy_micro()

    def connect_dummy_micro(self):
        self.sock_connect = socket.socket()
        port = 8000
        self.sock_connect.connect(("127.0.0.1", port))

    def readline(self):
        return self.sock_connect.recv(1024)
    
    def write(self, message):
        print("Writing message, {}".format(message))
        self.sock_connect.send(message)
