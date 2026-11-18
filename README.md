# Wayfarer Wanderer

The desktop host embeds Passenger's editor. Git records its source revision at
`vendor/passenger`. Install the .NET SDK named in `global.json` and Python 3,
then build from a recursive clone in its own directory.

```sh
git clone --recurse-submodules https://github.com/kzagoris/wayfarer-wanderer.git
cd wayfarer-wanderer
dotnet --version
dotnet restore WayfarerWanderer.slnx --locked-mode --configfile vendor/passenger/nuget.config
dotnet build WayfarerWanderer.slnx -c Debug --no-restore
rm -f artifacts/test-results/editorhost.trx
dotnet test tests/WayfarerWanderer.Tests -c Debug --no-build --filter FullyQualifiedName~EditorHostTests \
  --logger 'trx;LogFileName=editorhost.trx' --results-directory artifacts/test-results
python3 tools/check-trx.py artifacts/test-results/editorhost.trx WayfarerWanderer.Tests.EditorHostTests.TypingThenUndoRestoresTextAndCaret
```

After switching revisions, run `git submodule update --init --recursive` before restore.
On this workstation, `mise exec dotnet@11.0.100 -- dotnet` selects the pinned SDK.
