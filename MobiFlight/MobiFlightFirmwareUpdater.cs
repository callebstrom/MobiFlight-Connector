﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading.Tasks;

namespace MobiFlight
{
    static class MobiFlightFirmwareUpdater
    {
        /***
         * "D:\portableapps\arduino-1.0.5\hardware\tools\avr\bin\avrdude"
         */
        public static String ArduinoIdePath { get; set; }
        public static String AvrPath { get { return "hardware\\tools\\avr"; } }

        /***
         * C:\\Users\\SEBAST~1\\AppData\\Local\\Temp\\build2060068306446321513.tmp\\cmd_test_mega.cpp.hex
         **/
        public static String FirmwarePath { get; set; }

        public static bool IsValidArduinoIdePath(string path)
        {
            return Directory.Exists(path + "\\" + AvrPath);
        }

        public static bool IsValidFirmwareFilepath(string filepath)
        {
            return File.Exists(filepath);
        }

        public static bool Update(MobiFlightModule module)
        {
            string firmwareName;

            if (module.Board.AvrDudeSettings != null)
            {
                firmwareName = module.Board.Info.GetFirmwareName(module.Board.Info.LatestFirmwareVersion);
            }
            else if (module.Board.UsbDriveSettings != null)
            {
                firmwareName = module.Board.Info.GetFirmwareName(module.Board.Info.LatestFirmwareVersion);
            }
            else
            {
                Log.Instance.log($"Firmware update requested for {module.Board.Info.MobiFlightType} ({module.Port}) however no update settings were specified in the board definition file. Module update skipped.", LogSeverity.Error);
                return false;
            }

            return UpdateFirmware(module, firmwareName);
        }

        public static bool Reset(MobiFlightModule module)
        {
            if (String.IsNullOrEmpty(module.Board.Info.ResetFirmwareFile))
            {
                Log.Instance.log($"Firmware reset requested for {module.Board.Info.MobiFlightType} ({module.Port}) however no reset settings were specified in the board definition file. Module reset skipped.", LogSeverity.Error);
                return false;
            }

            return UpdateFirmware(module, module.Board.Info.ResetFirmwareFile);
        }

        public static bool UpdateFirmware(MobiFlightModule module, String FirmwareName)
        {
            bool result = false;
            String Port = "";
            
            // Only COM ports get toggled
            if (module.Port.StartsWith("COM"))
            { 
                module.InitUploadAndReturnUploadPort();
                if (module.Connected) module.Disconnect();
            }

            try
            {
                if (module.Board.AvrDudeSettings != null)
                {
                    while (!SerialPort.GetPortNames().Contains(Port))
                    {
                        System.Threading.Thread.Sleep(100);
                    }

                    RunAvrDude(Port, module.Board, FirmwareName);
                }
                else if (module.Board.UsbDriveSettings != null)
                {
                    FlashViaUsbDrive(module.Port, module.Board, FirmwareName);
                }
                else
                {
                    Log.Instance.log($"Firmware update requested for {module.Board.Info.MobiFlightType} ({module.Port}) however no update settings were specified in the board definition file. Module update skipped.", LogSeverity.Error);
                }

                if (module.Board.Connection.DelayAfterFirmwareUpdate > 0)
                {
                    System.Threading.Thread.Sleep(module.Board.Connection.DelayAfterFirmwareUpdate);
                }

                result = true;
            }
            catch
            {
                result = false;
            }
            return result;
        }

        public static void RunAvrDude(String port, Board board, String firmwareName) 
        {
            String message = "";

            if (!IsValidFirmwareFilepath(FirmwarePath + "\\" + firmwareName))
            {
                message = $"Firmware not found: {FirmwarePath}\\{firmwareName}.";
                Log.Instance.log(message, LogSeverity.Error);
                throw new FileNotFoundException(message);
            }

            String verboseLevel = "";
            
            //verboseLevel = " -v -v -v -v";

            String FullAvrDudePath = $@"{ArduinoIdePath}\{AvrPath}";

            foreach (var baudRate in board.AvrDudeSettings.BaudRates)
            {
                var proc1 = new ProcessStartInfo();
                var attempts = board.AvrDudeSettings.Attempts != null ? $" -x attempts={board.AvrDudeSettings.Attempts}" : "";
                string anyCommand =
                    $@"-C""{FullAvrDudePath}\etc\avrdude.conf""{verboseLevel}{attempts} -p{board.AvrDudeSettings.Device} -c{board.AvrDudeSettings.Programmer} -P{port} -b{baudRate} -D -Uflash:w:""{FirmwarePath}\{firmwareName}"":i";
                proc1.UseShellExecute = true;
                proc1.WorkingDirectory = $@"""{FullAvrDudePath}""";
                proc1.FileName = $@"""{FullAvrDudePath}\bin\avrdude""";
                proc1.Arguments = anyCommand;
                proc1.WindowStyle = ProcessWindowStyle.Hidden;
                Log.Instance.log($"{proc1.FileName} {anyCommand}", LogSeverity.Debug);
                Process p = Process.Start(proc1);
                if (p.WaitForExit(board.AvrDudeSettings.Timeout))
                {
                    Log.Instance.log($"Firmware upload exit code: {p.ExitCode}.", LogSeverity.Debug);
                    // everything OK
                    if (p.ExitCode == 0) return;

                    // process terminated but with an error.
                    message = $"ExitCode: {p.ExitCode} => Something went wrong when flashing with command \n {proc1.FileName} {anyCommand}.";
                }
                else
                {
                    // we timed out;
                    p.Kill();
                    message = $"avrdude timed out! Something went wrong when flashing with command \n {proc1.FileName} {anyCommand}.";
                }
                Log.Instance.log(message, LogSeverity.Error);
            }

            throw new Exception(message);
        }

        public static void FlashViaUsbDrive(String port, Board board, String firmwareName)
        {
            String FullFirmwarePath = $"{FirmwarePath}\\{firmwareName}";
            String message = "";
            DriveInfo driveInfo;

            if (!IsValidFirmwareFilepath(FullFirmwarePath))
            {
                message = $"Firmware not found: {FullFirmwarePath}";
                Log.Instance.log(message, LogSeverity.Error);
                throw new FileNotFoundException(message);
            }

            // Flashing can be initiated with either a COM port (for devices that weren't in bootsel mode to begin with
            // and got in that state by having the COM port toggled) or with a drive letter (for devices that were already
            // in bootsel mode when MobiFlight ran.
            // For boards that started as a COM port look up what the drive letter is after the port was toggled.
            if (port.StartsWith("COM"))
            {
                // Find all drives connected to the PC with a volume label that matches the one used to identify the 
                // drive that's the device to flash. This assumes the first matching drive is the one we want,
                // since it is extremely unlikely that more than one flashable USB drive will be connected and in a
                // flashable state at the same time.
                try
                {
                    driveInfo = DriveInfo.GetDrives().Where(d => d.VolumeLabel == board.UsbDriveSettings.VolumeLabel).First();
                }
                catch
                {
                    message = $"No mounted USB drives named {board.UsbDriveSettings.VolumeLabel} found.";
                    Log.Instance.log(message, LogSeverity.Error);
                    throw new FileNotFoundException(message);
                }
            }
            // For boards that were already a drive letter just get the drive info based off that.
            else
            {
                try
                {
                    driveInfo = new DriveInfo(port);
                }
                catch
                {
                    message = $"No mounted USB drive letter {port} found.";
                    Log.Instance.log(message, LogSeverity.Error);
                    throw new FileNotFoundException(message);
                }
            }

            // Look for the presence of a file on the drive to confirm it is really a drive that supports flashing via file copy.
            try
            {
                var verificationFile = driveInfo.RootDirectory.GetFiles(board.UsbDriveSettings.VerificationFileName).First();
            }
            catch
            {
                message = $"A mounted USB drive named {board.UsbDriveSettings.VolumeLabel} was found but verification file {board.UsbDriveSettings.VerificationFileName} was not found.";
                Log.Instance.log(message, LogSeverity.Error);
                throw new FileNotFoundException(message);
            }

            // At this point the drive is valid so all that's left is to copy the firmware over. If the file
            // copy succeeds the connected device will automatically reboot itself so there's no need to
            // attempt any kind of reconnect after either. Nice!
            var destination = $"{driveInfo.RootDirectory.FullName}{firmwareName}";
            try
            {
                Log.Instance.log($"Copying {FullFirmwarePath} to {destination}", LogSeverity.Debug);
                File.Copy(FullFirmwarePath, destination);
            }
            catch (Exception e)
            {
                message = $"Unable to copy {FullFirmwarePath} to {destination}: {e.Message}";
                Log.Instance.log(message, LogSeverity.Error);
                throw new Exception(message);
            }
        }
    }


}
