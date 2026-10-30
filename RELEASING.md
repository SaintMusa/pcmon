# Publishing a release

Push a version tag. GitHub then builds pcmon, checks that it runs, and publishes the zip on the Releases page.

```powershell
git tag v1.0.1
git push origin v1.0.1
```

The version comes from the tag, so a release cannot end up with the wrong number. Update `<Version>` in `pcmon.csproj` too, so that local builds show the same number.

If the workflow fails, nothing is published. Fix the problem, then delete the tag before trying again:

```powershell
git tag -d v1.0.1
git push origin --delete v1.0.1
```
