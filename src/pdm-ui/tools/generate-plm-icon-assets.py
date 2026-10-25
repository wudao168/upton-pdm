"""Compatibility entry point; all assets are generated from the shared SVG by Node."""
import subprocess
import sys
from pathlib import Path

if __name__ == "__main__":
    subprocess.run(["node", str(Path(__file__).with_suffix(".cjs")), *sys.argv[1:]], check=True)
