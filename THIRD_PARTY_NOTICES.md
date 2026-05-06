# Third-Party Notices

This project uses third-party OCR libraries and can optionally download an English OCR recognition model.

## PaddleOCRSharp

- Package: `PaddleOCRSharp`
- NuGet: https://www.nuget.org/packages/PaddleOCRSharp
- Project: https://github.com/raoyutian/PaddleOCRSharp

`PaddleOCRSharp` is used to run PaddleOCR inference from .NET.

## PaddleOCR

- Project: https://github.com/PaddlePaddle/PaddleOCR
- License: Apache License 2.0
- License text: https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE

PaddleOCR models and runtime components are used through `PaddleOCRSharp` and `Paddle.Runtime.win_x64`.

## Optional English OCR Model

- Model: `PaddlePaddle/en_PP-OCRv5_mobile_rec`
- Source: https://huggingface.co/PaddlePaddle/en_PP-OCRv5_mobile_rec
- License: Apache License 2.0

The English OCR model files are not stored in this repository. They can be downloaded with:

```powershell
.\scripts\download-english-ocr-model.ps1
```

Downloaded model files are placed under `Data/ocr-models/`, which is ignored by git.

If you distribute a release package that includes downloaded model files, include this notice and the applicable Apache License 2.0 notice with that package.
