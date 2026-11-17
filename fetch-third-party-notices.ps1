$ErrorActionPreference = "Stop"

$items = @(
    @{
        Name = "GTranslate"
        Version = "2.3.1"
        License = "MIT License"
        Project = "https://github.com/d4n3436/GTranslate"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/d4n3436/GTranslate/master/LICENSE" }
        )
    },
    @{
        Name = "NLog"
        Version = "5.0.4"
        License = "BSD 3-Clause License"
        Project = "https://github.com/NLog/NLog"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/NLog/NLog/dev/LICENSE.txt" }
        )
    },
    @{
        Name = "RapidOcrNet"
        Version = "3.0.0"
        License = "Apache License 2.0"
        Project = "https://github.com/BobLd/RapidOcrNet"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/BobLd/RapidOcrNet/master/LICENSE.txt" },
            @{ Label = "NOTICE"; Url = "https://raw.githubusercontent.com/BobLd/RapidOcrNet/master/NOTICE.txt" }
        )
    },
    @{
        Name = "PaddleOCR Models"
        Version = "PP-OCRv5 / PP-OCRv6 models used by OverTranslate"
        License = "Apache License 2.0"
        Project = "https://github.com/PaddlePaddle/PaddleOCR"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/LICENSE" }
        )
    },
    @{
        Name = "ONNX Runtime (DirectML)"
        Version = "1.24.4 (native); Microsoft.ML.OnnxRuntime.Managed 1.27.0"
        License = "MIT License"
        Project = "https://github.com/microsoft/onnxruntime"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/microsoft/onnxruntime/v1.24.4/LICENSE" }
        )
    },
    @{
        Name = "DirectML"
        Version = "Microsoft.AI.DirectML 1.15.4 (DirectML.dll, pulled in by ONNX Runtime DirectML)"
        License = "Microsoft Software License Terms (DirectML redistributable)"
        Project = "https://github.com/microsoft/DirectML"
        Files = @(
            @{ Label = "LICENSE.txt"; Url = "https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg"; Entry = "LICENSE.txt" },
            @{ Label = "ThirdPartyNotices.txt"; Url = "https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg"; Entry = "ThirdPartyNotices.txt" }
        )
    },
    @{
        Name = "OpenCV (ported source)"
        Version = "4.13.0"
        License = "Apache License 2.0; inpaint.cpp also carries the Intel License Agreement"
        Project = "https://github.com/opencv/opencv"
        Note = "OverTranslate does not ship OpenCV binaries. src/OverTranslate/Imaging ports two parts of OpenCV 4.13.0 to C#: image resizing (modules/imgproc/src/resize.cpp) and Navier-Stokes inpainting; the inpainting port comes from modules/photo/src/inpaint.cpp, whose original license header is reproduced below."
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/opencv/opencv/4.13.0/LICENSE" },
            @{ Label = "Intel License Agreement"; Url = "https://raw.githubusercontent.com/opencv/opencv/4.13.0/modules/photo/src/inpaint.cpp"; Until = "//M*/" }
        )
    },
    @{
        Name = "ZXing.Net"
        Version = "0.16.11"
        License = "Apache License 2.0"
        Project = "https://github.com/micjahn/ZXing.Net"
        Files = @(
            @{ Label = "COPYING"; Url = "https://raw.githubusercontent.com/micjahn/ZXing.Net/master/COPYING" }
        )
    },
)

$separator = "-" * 79
$builder = New-Object System.Text.StringBuilder

[void]$builder.AppendLine("THIRD-PARTY NOTICES")
[void]$builder.AppendLine()
[void]$builder.AppendLine("OverTranslate includes or uses the following third-party software and resources.")
[void]$builder.AppendLine("The license and notice text below is downloaded directly from the official upstream repositories and packages.")
[void]$builder.AppendLine()

foreach ($item in $items) {
    [void]$builder.AppendLine($separator)
    [void]$builder.AppendLine($item.Name)
    [void]$builder.AppendLine($separator)
    [void]$builder.AppendLine("Version: $($item.Version)")
    [void]$builder.AppendLine("Project: $($item.Project)")
    [void]$builder.AppendLine("License: $($item.License)")
    if ($item.Note) {
        [void]$builder.AppendLine("Note: $($item.Note)")
    }
    [void]$builder.AppendLine()

    foreach ($file in $item.Files) {
        Write-Host "Downloading $($item.Name) $($file.Label)..."
        if ($file.Entry) {
            # The license ships inside the NuGet package, not as a file of its own.
            Add-Type -AssemblyName System.IO.Compression
            $bytes = (Invoke-WebRequest -Uri $file.Url -UseBasicParsing).Content
            $zip = New-Object System.IO.Compression.ZipArchive((New-Object System.IO.MemoryStream(, $bytes)))
            $reader = New-Object System.IO.StreamReader($zip.GetEntry($file.Entry).Open(), [System.Text.Encoding]::UTF8)
            $content = $reader.ReadToEnd()
            $reader.Dispose()
            $zip.Dispose()
        }
        else {
            $content = (Invoke-WebRequest -Uri $file.Url -UseBasicParsing).Content
        }
        if ($file.Until) {
            # The license is the comment block at the top of a source file, not the whole file.
            $end = $content.IndexOf($file.Until)
            if ($end -lt 0) { throw "$($file.Url) has no '$($file.Until)'." }
            $content = $content.Substring(0, $end + $file.Until.Length)
        }
        $content = $content -replace "`r?`n", "`r`n"

        [void]$builder.AppendLine("[$($file.Label)]")
        if ($file.Entry) {
            [void]$builder.AppendLine("Source: $($file.Url) ($($file.Entry))")
        }
        else {
            [void]$builder.AppendLine("Source: $($file.Url)")
        }
        [void]$builder.AppendLine()
        [void]$builder.Append($content.TrimEnd())
        [void]$builder.AppendLine()
        [void]$builder.AppendLine()
    }
}

$output = Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt"
$text = $builder.ToString() -replace "(?<!`r)`n", "`r`n"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($output, $text, $utf8NoBom)

Write-Host "Created: $output"
