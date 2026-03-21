#!/usr/bin/env python3
"""
Rename RazorForge types to PascalCase.
Literal suffixes (e.g., 0_s32, 5u8) stay lowercase.
"""

import os
import re
import sys
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

# Type replacements - only full type names, not literal suffixes
TYPE_REPLACEMENTS = [
    # Signed integers (from largest to smallest to avoid partial matches)
    (r'\bs128\b', 'S128'), (r'\bs64\b', 'S64'), (r'\bs32\b', 'S32'),
    (r'\bs16\b', 'S16'), (r'\bs8\b', 'S8'),
    # Unsigned integers
    (r'\bu128\b', 'U128'), (r'\bu64\b', 'U64'), (r'\bu32\b', 'U32'),
    (r'\bu16\b', 'U16'), (r'\bu8\b', 'U8'),
    # Floats
    (r'\bf128\b', 'F128'), (r'\bf64\b', 'F64'), (r'\bf32\b', 'F32'),
    (r'\bf16\b', 'F16'),
    # Decimals
    (r'\bd128\b', 'D128'), (r'\bd64\b', 'D64'), (r'\bd32\b', 'D32'),
    # Address types
    (r'\bsaddr\b', 'SAddr'), (r'\buaddr\b', 'UAddr'),
    # Primitives
    (r'\bbool\b', 'Bool'), (r'\bbyte\b', 'Byte'), (r'\bletter\b', 'Letter'),
    # C types
    (r'\bcvoid\b', 'CVoid'), (r'\bcwchar\b', 'CWChar'), (r'\bcchar\b', 'CChar'),
    (r'\bcuchar\b', 'CUChar'), (r'\bcint\b', 'CInt'), (r'\bcuint\b', 'CUInt'),
    (r'\bclong\b', 'CLong'), (r'\bculong\b', 'CULong'),
    (r'\bclonglong\b', 'CLongLong'), (r'\bculonglong\b', 'CULongLong'),
    (r'\bcshort\b', 'CShort'), (r'\bcushort\b', 'CUShort'),
    (r'\bcfloat\b', 'CFloat'), (r'\bcdouble\b', 'CDouble'),
    (r'\bcstr\b', 'CStr'), (r'\bcwstr\b', 'CWStr'),
    # Preset constants
    (r'\bbyte_NULL\b', 'BYTE_NULL'), (r'\bbyte_SPACE\b', 'BYTE_SPACE'),
    (r'\bbyte_NEWLINE\b', 'BYTE_NEWLINE'), (r'\bbyte_TAB\b', 'BYTE_TAB'),
    (r'\bletter_NULL\b', 'LETTER_NULL'), (r'\bletter_SPACE\b', 'LETTER_SPACE'),
    (r'\bletter_NEWLINE\b', 'LETTER_NEWLINE'), (r'\bletter_TAB\b', 'LETTER_TAB'),
    (r'\bletter_REPLACEMENT\b', 'LETTER_REPLACEMENT'),
    (r'\bletter_MAX_UNICODE\b', 'LETTER_MAX_UNICODE'),
    (r'\bletter_SURROGATE_START\b', 'LETTER_SURROGATE_START'),
    (r'\bletter_SURROGATE_END\b', 'LETTER_SURROGATE_END'),
]

def rename_files(base_dir):
    """Rename files according to FILE_RENAMES."""
    count = 0
    for old_name, new_name in FILE_RENAMES.items():
        for old_path in base_dir.rglob(old_name):
            new_path = old_path.parent / new_name
            if old_path.exists() and not new_path.exists():
                os.rename(old_path, new_path)
                count += 1
                print(f"Renamed: {old_path.name} -> {new_name}")
    return count

def update_file_content(file_path):
    """Update type references in a file."""
    try:
        content = file_path.read_text(encoding='utf-8')
    except Exception as e:
        print(f"Error reading {file_path}: {e}")
        return False

    original = content
    for pattern, replacement in TYPE_REPLACEMENTS:
        content = re.sub(pattern, replacement, content)

    if content != original:
        try:
            file_path.write_text(content, encoding='utf-8')
            print(f"Updated: {file_path.name}")
            return True
        except Exception as e:
            print(f"Error writing {file_path}: {e}")
            return False
    return False

def update_all_files(base_dir, extensions):
    """Update all files with given extensions."""
    count = 0
    for ext in extensions:
        for file_path in base_dir.rglob(f'*{ext}'):
            if update_file_content(file_path):
                count += 1
    return count

def main():
    base_dir = Path(r'L:\programming\RiderProjects\RazorForge')
    stdlib_dir = base_dir / 'stdlib'
    wiki_dir = base_dir / 'wiki'

    print("=== Step 1: Renaming stdlib files ===", flush=True)
    renamed = rename_files(stdlib_dir)
    print(f"Renamed {renamed} files", flush=True)

    print("\n=== Step 2: Updating stdlib content ===", flush=True)
    updated_rf = update_all_files(stdlib_dir, ['.rf'])
    print(f"Updated {updated_rf} .rf files", flush=True)

    print("\n=== Step 3: Updating wiki content ===", flush=True)
    updated_md = update_all_files(wiki_dir, ['.md'])
    print(f"Updated {updated_md} .md files", flush=True)

    print("\n=== Done ===", flush=True)

if __name__ == '__main__':
    main()
