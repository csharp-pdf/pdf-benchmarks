# Contributing

## Methodology

The protocol in [METHODOLOGY.md](METHODOLOGY.md) is open to review. If a
measurement is unfair to a library, or misses something that matters in
production, open an issue explaining what and why. Changes to the methodology are
versioned, and every published result names the version it used.

## Library configuration

Each library should run the way its vendor recommends. If the configuration of a
library is wrong or suboptimal, open a pull request with the change and a link to
the documentation that recommends it. Vendors are welcome; please say in the pull
request that you represent the vendor.

## Results

Results are produced by the harness, not edited by hand. If you re-run the
benchmark on your own hardware and see something different, open an issue with
your `env.json` and raw results — that is useful evidence.

## Rules

- Never commit a licence key. Trial keys are supplied through environment
  variables or a git-ignored local file.
- Fixtures must have a licence that allows redistribution; record it in
  [fixtures/README.md](fixtures/README.md).

By contributing you agree that your contributions are licensed under the
[MIT License](LICENSE).
