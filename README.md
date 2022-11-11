## Civitai model scanner
A model scanner for malware and dangerous pickles

### Usage
```bash
$ docker build -t civitai-model-scanner . 
$ docker run -it --rm civitai-model-scanner 'https://huggingface.co/ykilcher/totally-harmless-model/resolve/main/pytorch_model.bin'
```
output:
```json
{
    // The URL that was scanned
   "url": "https://huggingface.co/ykilcher/totally-harmless-model/resolve/main/pytorch_model.bin",
   // 0: No malware, 1: Found malware, 2: Error
   "pickescanExitCode": 1,
   "pickescanOutput": "/model.bin:archive/data.pkl: dangerous import '__builtin__ eval' FOUND\n----------- SCAN SUMMARY -----------\nScanned files: 1\nInfected files: 1\nDangerous globals: 1",
   // 0: No malware, 1: Found malware, 2: Error
   "clamscanExitCode": 0,
   "clamscanOutput": "clamscan"
}
```

## Links
- https://huggingface.co/docs/hub/security-pickle
- https://github.com/mmaitre314/picklescan/tree/main