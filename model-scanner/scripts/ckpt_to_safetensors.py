from sys import argv
import torch
from safetensors.torch import save_file

path = argv[1]
newPath = argv[2]

print(f"Loading {path}")

with torch.no_grad():
    weights = torch.load(path)
    if 'state_dict' in weights:
        weights.pop('state_dict')

    print(f"Saving {newPath}")    
    save_file(weights, newPath)
