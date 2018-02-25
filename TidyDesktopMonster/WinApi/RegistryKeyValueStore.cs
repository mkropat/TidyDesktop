﻿using System.IO;
using Microsoft.Win32;
using TidyDesktopMonster.Interface;
using TidyDesktopMonster.KeyValueStore;

namespace TidyDesktopMonster.WinApi
{
    public class RegistryKeyValueStore : IKeyValueStore
    {
        readonly RegistryHive _hive;
        readonly string _regPath;

        public RegistryKeyValueStore(string packageName, string vendorName = null, RegistryHive hive = RegistryHive.CurrentUser)
        {
            _hive = hive;
            _regPath = string.IsNullOrEmpty(vendorName)
                ? Path.Combine("Software", packageName)
                : Path.Combine("Software", vendorName, packageName);
        }

        public T Read<T>(string key)
        {
            using (var root = OpenRoot())
            using (var regKey = root.OpenSubKey(_regPath))
            {
                return regKey == null
                    ? default
                    : TypeConverter.CoerceToType<T>(regKey.GetValue(key));
            }
        }

        public void Write<T>(string key, T value)
        {
            using (var root = OpenRoot())
            using (var subkey = root.CreateSubKey(_regPath))
            {
                switch (value)
                {
                    case bool v:
                        subkey.SetValue(key, v, RegistryValueKind.DWord);
                        break;

                    default:
                        subkey.SetValue(key, value);
                        break;
                }
            }
        }

        RegistryKey OpenRoot()
        {
            return RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
        }
    }
}
