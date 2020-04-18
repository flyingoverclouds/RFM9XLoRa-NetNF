﻿//---------------------------------------------------------------------------------
// Copyright (c) April 2020, devMobile Software
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// nanoff --target ST_STM32F429I_DISCOVERY --update
//---------------------------------------------------------------------------------
namespace devMobile.IoT.Rfm9x.TransmitBasic
{
   using System;
   using System.Text;
   using System.Threading;

   using Windows.Devices.Gpio;
   using Windows.Devices.Spi;

   public sealed class Rfm9XDevice
   {
      private SpiDevice rfm9XLoraModem;
      private GpioPin chipSelectGpioPin;
      private const byte RegisterAddressReadMask = 0X7f;
      private const byte RegisterAddressWriteMask = 0x80;

      public Rfm9XDevice(string spiPort, int chipSelectPin, int resetPin)
      {
         var settings = new SpiConnectionSettings(chipSelectPin)
         {
            ClockFrequency = 500000,
//            DataBitLength = 8,
            Mode = SpiMode.Mode0,// From SemTech docs pg 80 CPOL=0, CPHA=0
            SharingMode = SpiSharingMode.Shared,
         };

         rfm9XLoraModem = SpiDevice.FromId(spiPort, settings);

         GpioController gpioController = GpioController.GetDefault();

         // Chip select pin configuration
         chipSelectGpioPin = gpioController.OpenPin(chipSelectPin);
         chipSelectGpioPin.SetDriveMode(GpioPinDriveMode.Output);

         // Factory reset pin configuration
         GpioPin resetGpioPin = gpioController.OpenPin(resetPin);
         resetGpioPin.SetDriveMode(GpioPinDriveMode.Output);
         resetGpioPin.Write(GpioPinValue.Low);
         Thread.Sleep(10);
         resetGpioPin.Write(GpioPinValue.High);
         Thread.Sleep(10);
      }

      public Byte RegisterReadByte(byte registerAddress)
      {
         byte[] writeBuffer = new byte[] { registerAddress &= RegisterAddressReadMask, 0x0 };
         byte[] readBuffer = new byte[writeBuffer.Length];

         rfm9XLoraModem.TransferFullDuplex(writeBuffer, readBuffer);

         return readBuffer[1];
      }

      public ushort RegisterReadWord(byte address)
      {
         byte[] writeBuffer = new byte[] { address &= RegisterAddressReadMask, 0x0, 0x0 };
         byte[] readBuffer = new byte[writeBuffer.Length];

         rfm9XLoraModem.TransferFullDuplex(writeBuffer, readBuffer);

         return (ushort)(readBuffer[2] + (readBuffer[1] << 8));
      }

      public byte[] RegisterRead(byte address, int length)
      {
         byte[] writeBuffer = new byte[length + 1];
         byte[] readBuffer = new byte[length + 1];
         byte[] repyBuffer = new byte[length];

         writeBuffer[0] = address &= RegisterAddressReadMask;

         rfm9XLoraModem.TransferFullDuplex(writeBuffer, readBuffer);

         Array.Copy(readBuffer, 1, repyBuffer, 0, length);

         return repyBuffer;
      }

      public void RegisterWriteByte(byte address, byte value)
      {
         byte[] writeBuffer = new byte[] { address |= RegisterAddressWriteMask, value };

         rfm9XLoraModem.Write(writeBuffer);
      }

      public void RegisterWriteWord(byte address, ushort value)
      {
         byte[] valueBytes = BitConverter.GetBytes(value);
         byte[] writeBuffer = new byte[] { address |= RegisterAddressWriteMask, valueBytes[0], valueBytes[1] };

         rfm9XLoraModem.Write(writeBuffer);
      }

      public void RegisterWrite(byte address, byte[] bytes)
      {
         byte[] writeBuffer = new byte[1 + bytes.Length];

         Array.Copy(bytes, 0, writeBuffer, 1, bytes.Length);
         writeBuffer[0] = address |= RegisterAddressWriteMask;

         rfm9XLoraModem.Write(writeBuffer);
      }

      public void RegisterDump()
      {
         Console.WriteLine("Register dump");
         for (byte registerIndex = 0; registerIndex <= 0x42; registerIndex++)
         {
            byte registerValue = this.RegisterReadByte(registerIndex);

            Console.WriteLine($"Register 0x{registerIndex:x2} - Value 0X{registerValue:x2}");
         }
      }
   }

   class Program
   {
      static void Main()
      {
         Rfm9XDevice rfm9XDevice = new Rfm9XDevice("SPI5", PinNumber('C', 2), PinNumber('C', 3));
         int SendCount = 0;

         // Put device into LoRa + Sleep mode
         rfm9XDevice.RegisterWriteByte(0x01, 0b10000000); // RegOpMode 

         // Set the frequency to 915MHz
         byte[] frequencyWriteBytes = { 0xE4, 0xC0, 0x00 }; // RegFrMsb, RegFrMid, RegFrLsb
         rfm9XDevice.RegisterWrite(0x06, frequencyWriteBytes);

         // More power PA Boost
         rfm9XDevice.RegisterWriteByte(0x09, 0b10000000); // RegPaConfig

         rfm9XDevice.RegisterDump();

         while (true)
         {
            rfm9XDevice.RegisterWriteByte(0x0E, 0x0); // RegFifoTxBaseAddress 

            // Set the Register Fifo address pointer
            rfm9XDevice.RegisterWriteByte(0x0D, 0x0); // RegFifoAddrPtr 

            string messageText = $"Hello LoRa {SendCount += 1}!";

            // load the message into the fifo
            byte[] messageBytes = UTF8Encoding.UTF8.GetBytes(messageText);
            rfm9XDevice.RegisterWrite(0x0, messageBytes); // RegFifo

            // Set the length of the message in the fifo
            rfm9XDevice.RegisterWriteByte(0x22, (byte)messageBytes.Length); // RegPayloadLength

            Console.WriteLine($"Sending {messageBytes.Length} bytes message {messageText}");
            /// Set the mode to LoRa + Transmit
            rfm9XDevice.RegisterWriteByte(0x01, 0b10000011); // RegOpMode 

            // Wait until send done, no timeouts in PoC
            Console.WriteLine("Send-wait");
            byte IrqFlags = rfm9XDevice.RegisterReadByte(0x12); // RegIrqFlags
            while ((IrqFlags & 0b00001000) == 0)  // wait until TxDone cleared
            {
               Thread.Sleep(10);
               IrqFlags = rfm9XDevice.RegisterReadByte(0x12); // RegIrqFlags
               Console.WriteLine(".");
            }
            rfm9XDevice.RegisterWriteByte(0x12, 0b00001000); // clear TxDone bit
            Console.WriteLine("Send-Done");

            Thread.Sleep(10000);
         }
      }

      static int PinNumber(char port, byte pin)
      {
         if (port < 'A' || port > 'J')
            throw new ArgumentException();

         return ((port - 'A') * 16) + pin;
      }
   }
}