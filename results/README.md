# Results

One folder per run: `<yyyy-mm-dd>-<os>/`.

```
2026-xx-xx-windows/
  env.json          hardware, OS build, .NET SDK/runtime, methodology version
  packages.json     exact package id + version for every library in the run
  speed.csv         library, fixture, run, ms
  throughput.csv    library, fixture, concurrency, conversions_per_minute
  memory.csv        library, fixture, peak_working_set_mb
  output.csv        library, fixture, bytes, pages, fonts_embedded, text_extractable
  fidelity.csv      library, fixture, tile, pixel_diff_pct, ssim, pages, height_ratio
  checks.csv        library, fixture, check, pass
  standards.csv     library, standard, verapdf_result
  deployment.csv    library, os, image_mb, extra_packages, non_root_ok, publish_mb
```

Files are written by the harness and never edited by hand. Older runs are kept.
