# Results

One folder per run: `<yyyy-mm-dd>-<os>/`.

```
2026-xx-xx-windows/
  env.json            hardware, OS, .NET runtime, methodology version, run settings
  packages.json       exact package ids and versions for every library in the run
  outputs.csv         library, fixture, ok, ms, pages, bytes, error      (one conversion each, PDFs kept)
  speed.csv           library, fixture, round, ok, ms, pages, bytes, error
  memory.csv          library, fixture, parallel, peak_pss_mb, processes_at_peak
  cold.csv            library, run, process_start_to_pdf_ms, conversion_ms, ok, error
  throughput.csv      library, fixture, mode, concurrency, seconds, completed, failed, per_minute
  output.csv          library, fixture, bytes, pages, fonts, fonts_embedded, text_chars
  checks.csv          library, fixture, check, expected, pass, detail
  fidelity.csv        library, fixture, pages, height_ratio, ssim_mean, ssim_worst,
                      pixel_diff_pct, max_offset_px, masked_pct
  fidelity_tiles.csv  library, fixture, y, offset, ssim, pixel_diff_pct
  standards_runs.csv  library, standard, ok, unsupported, ms, pages, bytes, error
  standards.csv       library, standard, profile, compliant, failed_rules, rules   (veraPDF)
  structure.csv       library, standard, check, expected, found, pass              (tag content)
  parallel_runs.csv   library, mode, jobs, ms, failed, errors
  parallel.csv        library, mode, fixture, copy, pages_sequential, pages_parallel, text_same, max_pixel_diff_pct, same
  errors.csv          library, case, outcome, ms, pages, error, recovered
  errors_checks.csv   library, case, expected, outcome, ms, recovered, pass, detail
  stability.csv       library, conversions, pss_mb, processes, median_ms, failures
  tools_runs.csv      library, tool, input, ok, unsupported, ms, result, error
  extraction.csv      library, input, pages_expected, pages, text_f1
  search.csv          library, input, term, expected_pages, found_pages, hits, pass
  raster.csv          library, input, page, width, height, expected_width, expected_height, size_ok, ssim_vs_pdfium
  word.csv            library, input, pages_word, pages, text_f1_vs_word, ssim_mean, ssim_worst, fonts_embedded, images
  redaction.csv       library, check, term, pass, detail
  merge_split.csv     library, operation, check, pass, detail
  watermark.csv       library, check, pass, detail
  signature.csv       library, check, pass, detail                              (pyHanko)
  fill.csv            library, field, value, pass, detail
  einvoice.csv        library, check, pass, detail
  pdfx.csv            library, check, pass, detail
  deployment.csv      library, variant, extra_os_packages, image_mb, root_ok, root_seconds, non_root_ok, non_root_seconds, error
  publish_size.csv    library, rid, publish_mb
```

Files are written by the harness and never edited by hand. Older runs are kept.
