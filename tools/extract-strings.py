"""Lists every English UI text in the app (Loc.T("...") and {ui:T '...'}), one per line, sorted.

    python tools/extract-strings.py > tools/strings.txt
"""
import glob
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

root = os.path.join(os.path.dirname(__file__), '..', 'src', 'JsyncDrive')
code = re.compile(r'Loc\.T\(\s*"((?:[^"\\]|\\.)*)"')
xaml = re.compile(r"\{ui:T '((?:[^'\\]|\\.)*)'\}")
found = set()
for p in glob.glob(os.path.join(root, '**', '*'), recursive=True):
    if not p.endswith(('.cs', '.xaml')) or p.endswith('Spanish.cs'):
        continue
    s = open(p, encoding='utf-8').read()
    found.update(m.group(1).replace('\\"', '"') for m in code.finditer(s))
    found.update(m.group(1).replace("\\'", "'") for m in xaml.finditer(s))
# texts that reach Loc.T through a variable
found.update(["Never", "7 days", "30 days", "90 days", "Same as Windows", "Light", "Dark", "Blue", "Green", "Yellow", "Red",
              "Purple", "Orange", "Pink", "Cyan", "Grey", "Default", "Unlock"])
print('\n'.join(sorted(found)))
