# RouteBand

Simulator, experiment runners, analysis programs and per graph results for the
paper "RouteBand: A Novel Relay Selection Mechanism Using Route Exposure under
Limited Fanout in Unstructured P2P Networks". Every number in the paper can be recomputed
from what is here.

## Layout

```
src/           C# simulator
experiments/   runners, one per experiment
analysis/      analysis programs
results/       per graph summaries, JSON results, Gnutella outputs
gnutella/      the nine SNAP snapshots and the measured topology experiment
```

## Setup

Requires .NET 7.0 and Python 3.11 or later.

```bash
python3 -m pip install -r requirements.txt
dotnet build src/ScalableP2P/Diagnostics.csproj -c Release
```

## Running the experiments

Every experiment is seeded, so two runs with the same seed produce the same
output. Each runner is resumable; rerunning skips completed work.

```bash
cd experiments
./run_confirmation.sh          # the confirmatory contrast on 256 fresh graphs
./run_target_observing.sh      # calibration allowed to observe the item
./run_target_disentangle.sh    # isolation and conditioning separated
./run_fanout_sweep.sh          # m = 3, 5, 7, 11
./run_threshold.sh             # tau = 0.90, 0.95, 0.975 at fanout 7
./run_adaptive_slicing.sh      # three feasibility regimes
./run_calibration_budget.sh    # K = 100, 200, 500
./run_neutral_diagnostic.sh    # complete relay sets at exact degrees 8, 20, 24
./run_ten_arm_confirmation.sh  # the ten arm gate battery on the design seeds
cd ..
python3 analysis/analyze_confirmation.py experiments/runs
python3 analysis/analyze_target_observing.py experiments/runs
python3 analysis/analyze_disentangle.py experiments/runs
python3 analysis/analyze_fanout_sweep.py
```

The confirmation should report +2.687 and +2.138 percentage points with lower
bounds +2.340 and +1.844.

Measured Gnutella snapshots:

```bash
cd gnutella
python3 test_gnutella_portability.py
python3 run_gnutella_portability.py --data-dir . --output-dir out --workers 3 --fanout 7
python3 analyze_gnutella_portability.py --run-dir out --data-dir . --output-dir out-analysis
```

`--fanout` also accepts 11 and 15.

## Results without rerunning

`results/summaries/` holds one compressed bundle of per graph summary CSVs per
experiment, and the analysis programs run directly on an unpacked bundle:

```bash
mkdir -p /tmp/confirmation && tar xzf results/summaries/confirmation.tar.gz -C /tmp/confirmation
python3 analysis/analyze_confirmation.py /tmp/confirmation
```

`results/supplementary/` holds the same results as JSON, including the per
graph differences behind every reported contrast. Row level probe records are
not kept: the runners write them during a run and delete them when the run
completes, since no analysis reads them. To keep them, remove the `rm -f
"$OUT"/*-probes.csv` line from the runner; the seeds regenerate them exactly.

## License

MIT. See `LICENSE`.
