#!/usr/bin/env python3
"""Fix cross-wiki URLs to include /docs/ path."""
import os
import re

SUFLAE_WIKI = r"L:\programming\RiderProjects\RazorForge\Suflae-Wiki\docs"
RAZORFORGE_WIKI = r"L:\programming\RiderProjects\RazorForge\RazorForge-Wiki\docs"

def fix_urls(wiki_path):
    """Fix URLs to include /docs/ path."""
    for filename in os.listdir(wiki_path):
        if not filename.endswith('.md'):
            continue
        filepath = os.path.join(wiki_path, filename)
        with open(filepath, 'r', encoding='utf-8') as f:
            content = f.read()

        original = content

        # Fix suflae.lumi-dev.xyz/XXX/ -> suflae.lumi-dev.xyz/docs/XXX/
        content = re.sub(
            r'(https://suflae\.lumi-dev\.xyz)/([A-Za-z0-9-]+)/',
            r'\1/docs/\2/',
            content
        )

        # Fix razorforge.lumi-dev.xyz/XXX/ -> razorforge.lumi-dev.xyz/docs/XXX/
        content = re.sub(
            r'(https://razorforge\.lumi-dev\.xyz)/([A-Za-z0-9-]+)/',
            r'\1/docs/\2/',
            content
        )

        if content != original:
            with open(filepath, 'w', encoding='utf-8') as f:
                f.write(content)
            print(f"Fixed: {filename}")

if __name__ == '__main__':
    print("Fixing URLs in Suflae wiki...")
    fix_urls(SUFLAE_WIKI)
    print("\nFixing URLs in RazorForge wiki...")
    fix_urls(RAZORFORGE_WIKI)
    print("\nDone!")