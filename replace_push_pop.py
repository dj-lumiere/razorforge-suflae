import os
import re

wiki_dir = r"L:\programming\RiderProjects\RazorForge\wiki"

for filename in os.listdir(wiki_dir):
    if filename.endswith('.md'):
        filepath = os.path.join(wiki_dir, filename)
        with open(filepath, 'r', encoding='utf-8') as f:
            content = f.read()

        original = content

        # Replace .push( with .add_last(
        content = re.sub(r'\.push\(', '.add_last(', content)

        # Replace .pop() and .pop!() with .remove_last!() for List (stack) context
        # Most cases are List (stack), so use remove_last!
        content = re.sub(r'\.pop\(\)', '.remove_last!()', content)
        content = re.sub(r'\.pop!\(\)', '.remove_last!()', content)

        if content != original:
            with open(filepath, 'w', encoding='utf-8') as f:
                f.write(content)
            print(f"Updated: {filename}")

print("Done!")