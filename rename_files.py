#!/usr/bin/env python3
"""Rename files with case changes on Windows (case-insensitive FS)."""

import os
from pathlib import Path

# File renames (old_name -> new_name)
FILE_RENAMES = {
    # NativeDataTypes
    's8.rf': 'S8.rf', 's16.rf': 'S16.rf', 's32.rf': 'S32.rf',
    's64.rf': 'S64.rf', 's128.rf': 'S128.rf',
    'u8.rf': 'U8.rf', 'u16.rf': 'U16.rf', 'u32.rf': 'U32.rf',
    'u64.rf': 'U64.rf', 'u128.rf': 'U128.rf',
    'f16.rf': 'F16.rf', 'f32.rf': 'F32.rf', 'f64.rf': 'F64.rf',
    'f128.rf': 'F128.rf',
    'd32.rf': 'D32.rf', 'd64.rf': 'D64.rf', 'd128.rf': 'D128.rf',
    'saddr.rf': 'SAddr.rf', 'uaddr.rf': 'UAddr.rf',
    'bool.rf': 'Bool.rf',
    # Text types
    'byte.rf': 'Byte.rf', 'letter.rf': 'Letter.rf',
    # FFI types
    'ctypes.rf': 'CTypes.rf', 'cstr.rf': 'CStr.rf', 'cwstr.rf': 'CWStr.rf',
}

def rename_with_temp(old_path, new_name):
    """Rename using temp file to handle case-insensitive FS."""
    temp_path = old_path.parent / (old_path.name + '.tmp')
    new_path = old_path.parent / new_name

    # Step 1: rename to temp
    os.rename(old_path, temp_path)
    # Step 2: rename to final
    os.rename(temp_path, new_path)
    print(f"Renamed: {old_path.name} -> {new_name}")

def main():
    stdlib_dir = Path(r'L:\programming\RiderProjects\RazorForge\stdlib')

    count = 0
    for old_name, new_name in FILE_RENAMES.items():
        for old_path in stdlib_dir.rglob(old_name):
            if old_path.exists():
                rename_with_temp(old_path, new_name)
                count += 1

    print(f"\nRenamed {count} files")

if __name__ == '__main__':
    main()
