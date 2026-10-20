"""Lists the English UI texts that have no Spanish yet, and the Spanish entries nothing uses.

    python tools/check-strings.py
"""
import os
import re
import subprocess
import sys

here = os.path.dirname(__file__)
english = set(subprocess.check_output([sys.executable, os.path.join(here, 'extract-strings.py')], text=True, encoding='utf-8').splitlines())
spanish_src = open(os.path.join(here, '..', 'src', 'JsyncDrive', 'Ui', 'Spanish.cs'), encoding='utf-8').read()
spanish = set(m.group(1).replace('\\\\', '\\') for m in re.finditer(r'\["((?:[^"\\]|\\.)*)"\] =', spanish_src))
english = set(e.replace('\\\\', '\\') for e in english)
missing = sorted(english - spanish)
unused = sorted(spanish - english)
print(f'{len(english)} texts, {len(spanish)} translated')
for m in missing:
    print('MISSING ', m)
for u in unused:
    print('UNUSED  ', u)
sys.exit(1 if missing else 0)
