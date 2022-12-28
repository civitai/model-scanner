from sys import argv
import torch
from safetensors.torch import load_file

path = argv[1]
newPath = argv[2]

print(f"Loading {path}")

with torch.no_grad():
    weights = load_file(path, device = "cpu")

    print(f"Saving {newPath}")    
    torch.save(weights, newPath)
