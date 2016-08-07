// Install addins.
#addin "nuget:?package=Polly&version=4.2.0"

// Install tools.
#tool "nuget:?package=GitReleaseNotes"
#tool "nuget:?package=GitVersion.CommandLine"
#tool "nuget:?package=gitreleasemanager"
#tool "nuget:?package=OpenCover"
#tool "nuget:?package=ReportGenerator"
#tool "nuget:?package=coveralls.io"

// Using statements
using Polly;


///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////

var target = Argument<string>("target", "Default");
var configuration = Argument<string>("configuration", "Release");


///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////

var libraryName = "Picton";
var gitHubAccountName = "Jericho";
var gitHubRepo = "Picton";

var nuGetApiUrl = EnvironmentVariable("NUGET_API_URL");	// nuget.org is used if this value is omitted
var nuGetApiKey = EnvironmentVariable("NUGET_API_KEY");
var gitHubUserName = EnvironmentVariable("GITHUB_USERNAME");
var gitHubPassword = EnvironmentVariable("GITHUB_PASSWORD");

var solutions = GetFiles("./*.sln");
var solutionPaths = solutions.Select(solution => solution.GetDirectory());
var unitTestsPaths = GetDirectories("./*.UnitTests");
var outputDir = "./artifacts/";
var versionInfo = GitVersion(new GitVersionSettings() { OutputType = GitVersionOutput.Json });
var milestone = string.Concat("v", versionInfo.MajorMinorPatch);
var cakeVersion = typeof(ICakeContext).Assembly.GetName().Version.ToString();
var isLocalBuild = BuildSystem.IsLocalBuild;
var isMainBranch = StringComparer.OrdinalIgnoreCase.Equals("master", BuildSystem.AppVeyor.Environment.Repository.Branch);
var	isMainRepo = StringComparer.OrdinalIgnoreCase.Equals(gitHubAccountName + "/" + gitHubRepo, BuildSystem.AppVeyor.Environment.Repository.Name);
var	isPullRequest = BuildSystem.AppVeyor.Environment.PullRequest.IsPullRequest;
var	isTagged = (
	BuildSystem.AppVeyor.Environment.Repository.Tag.IsTag &&
	!string.IsNullOrWhiteSpace(BuildSystem.AppVeyor.Environment.Repository.Tag.Name)
);


///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(context =>
{
	if (isMainBranch && (context.Log.Verbosity != Verbosity.Diagnostic))
	{
		Information("Increasing verbosity to diagnostic.");
		context.Log.Verbosity = Verbosity.Diagnostic;
	}

	Information("Building version {0} of {1} ({2}, {3}) using version {4} of Cake",
		versionInfo.LegacySemVerPadded,
		libraryName,
		configuration,
		target,
		cakeVersion
	);

	Information("Variables:\r\n\tLocalBuild: {0}\r\n\tIsMainBranch: {1}\r\n\tIsMainRepo: {2}\r\n\tIsPullRequest: {3}\r\n\tIsTagged: {4}",
		isLocalBuild,
		isMainBranch,
		isMainRepo,
		isPullRequest,
		isTagged
	);

	Information("Nuget Info:\r\n\tApi Url: {0}\r\n\tApi Key: {1}",
		nuGetApiUrl,
		string.IsNullOrEmpty(nuGetApiKey) ? "[NULL]" : new string('*', nuGetApiKey.Length)
	);

	Information("GitHub Info:\r\n\tAccount: {0}\r\n\tRepo: {1}\r\n\tUserName: {2}\r\n\tPassword: {3}",
		gitHubAccountName,
		gitHubRepo,
		gitHubUserName,
		string.IsNullOrEmpty(gitHubPassword) ? "[NULL]" : new string('*', gitHubPassword.Length)
	);
});

Teardown(context =>
{
	// Executed AFTER the last task.
	Information("Finished running tasks.");
});


///////////////////////////////////////////////////////////////////////////////
// TASK DEFINITIONS
///////////////////////////////////////////////////////////////////////////////

Task("Clean")
	.Does(() =>
{
	// Clean solution directories.
	foreach(var path in solutionPaths)
	{
		Information("Cleaning {0}", path);
		CleanDirectories(path + "/*/bin/" + configuration);
		CleanDirectories(path + "/*/obj/" + configuration);
	}

	// Clean previous artifacts
	Information("Cleaning {0}", outputDir);
	if (DirectoryExists(outputDir))
	{
		CleanDirectories(MakeAbsolute(Directory(outputDir)).FullPath);
	}
	else
	{
		CreateDirectory(outputDir);
	}
});

Task("Restore-NuGet-Packages")
	.IsDependentOn("Clean")
	.Does(() =>
{
	// Restore all NuGet packages.
	foreach(var solution in solutions)
	{
		var maxRetryCount = 5;
		var toolTimeout = 1d;

		Information("Restoring {0}...", solution);

		Policy
			.Handle<Exception>()
			.Retry(maxRetryCount, (exception, retryCount, context) => {
				if (retryCount == maxRetryCount)
				{
					throw exception;
				}
				else
				{
					Verbose("{0}", exception);
					toolTimeout += 0.5;
				}})
			.Execute(()=> {
				NuGetRestore(solution, new NuGetRestoreSettings {
					Source = new List<string> {
						"https://api.nuget.org/v3/index.json",
						"https://www.myget.org/F/roslyn-nightly/api/v3/index.json"
					},
					ToolTimeout = TimeSpan.FromMinutes(toolTimeout)
				});
			});
	}
});

Task("Update-Asembly-Version")
	.Does(() =>
{
	GitVersion(new GitVersionSettings()
	{
		UpdateAssemblyInfo = true,
		OutputType = GitVersionOutput.BuildServer
	});
});

Task("Build")
	.IsDependentOn("Restore-NuGet-Packages")
	.IsDependentOn("Update-Asembly-Version")
	.Does(() =>
{
	// Build all solutions.
	foreach(var solution in solutions)
	{
		Information("Building {0}", solution);
		MSBuild(solution, new MSBuildSettings()
			.SetPlatformTarget(PlatformTarget.MSIL)
			.SetConfiguration(configuration)
			.SetVerbosity(Verbosity.Minimal)
			.SetNodeReuse(false)
			.WithProperty("Windows", "True")
			.WithProperty("TreatWarningsAsErrors", "True")
			.WithTarget("Build")
		);
	}
});

Task("Run-Unit-Tests")
	.IsDependentOn("Build")
	.Does(() =>
{
	foreach(var path in unitTestsPaths)
	{
		Information("Running unit tests in {0}...", path);
		MSTest(path + "/bin/" + configuration + "/*.UnitTests.dll");
	}
});

Task("Run-Code-Coverage")
	.IsDependentOn("Build")
	.Does(() =>
{
	OpenCover(
		tool => { tool.MSTest("./*.UnitTests/bin/" + configuration + "/*.UnitTests.dll"); },
		new FilePath("./CodeCoverageData/coverage.xml"),
		new OpenCoverSettings()
			.ExcludeByAttribute("*.ExcludeFromCodeCoverage*")
			.WithFilter("+[Picton]*")
			.WithFilter("-[Picton]Picton.Properties.*")
	);
});

Task("Upload-Coverage-Result")
	.Does(() =>
{
	StartProcess("./tools/coveralls.io/tools/coveralls.net.exe", "--opencover ./CodeCoverageData/coverage.xml");
});

Task("Generate-Code-Coverage-Report")
	.IsDependentOn("Run-Code-Coverage")
	.Does(() =>
{
	ReportGenerator(
		"./CodeCoverageData/*.xml",
		"./CodeCoverageReport",
		new ReportGeneratorSettings() {
			ClassFilters = new[] { "*.UnitTests*" }
		}
	);
});

Task("Create-NuGet-Package")
	.IsDependentOn("Build")
	.Does(() =>
{
	var settings = new NuGetPackSettings
	{
		Id                      = libraryName,
		Version                 = versionInfo.NuGetVersionV2,
		Title                   = "The Picton library for Azure",
		Authors                 = new[] { "Jeremie Desautels" },
		Owners                  = new[] { "Jeremie Desautels" },
		Description             = "Convenient library for Azure",
		Summary                 = "Among other things, it contains extension methods and abstrations for StorageAccount, BlobClient, QueueClient, etc.",
		ProjectUrl              = new Uri("https://github.com/Jericho/Picton"),
		IconUrl                 = new Uri("https://github.com/identicons/jericho.png"),
		LicenseUrl              = new Uri("http://jericho.mit-license.org"),
		Copyright               = "Copyright (c) 2016 Jeremie Desautels",
		ReleaseNotes            = new [] { "Initial release" },
		Tags                    = new [] { "Picton", "Azure" },
		RequireLicenseAcceptance= false,
		Symbols                 = false,
		NoPackageAnalysis       = true,
		Dependencies            = new [] {
			new NuSpecDependency { Id = "Newtonsoft.Json", Version = "9.0.1" },
			new NuSpecDependency { Id = "WindowsAzure.Storage", Version = "7.1.2" }
		},
		Files                   = new [] {
			new NuSpecContent { Source = libraryName + ".45/bin/" + configuration + "/" + libraryName + ".dll", Target = "lib/net45" },
			new NuSpecContent { Source = libraryName + ".451/bin/" + configuration + "/" + libraryName + ".dll", Target = "lib/net451" },
			new NuSpecContent { Source = libraryName + ".452/bin/" + configuration + "/" + libraryName + ".dll", Target = "lib/net452" },
			new NuSpecContent { Source = libraryName + ".46/bin/" + configuration + "/" + libraryName + ".dll", Target = "lib/net46" },
			new NuSpecContent { Source = libraryName + ".461/bin/" + configuration + "/" + libraryName + ".dll", Target = "lib/net461" }
		},
		BasePath                = "./",
		OutputDirectory         = outputDir,
		ArgumentCustomization   = args => args.Append("-Prop Configuration=" + configuration)
	};
			
	NuGetPack(settings);
});

Task("Create-Release-Notes")
	.Does(() =>
{
	GitReleaseNotes(outputDir + "releasenotes.md", new GitReleaseNotesSettings {
		WorkingDirectory         = ".",
		AllLabels                = true,
		AllTags                  = true,
		Verbose                  = true
	});
});

Task("Upload-AppVeyor-Artifacts")
	.WithCriteria(() => AppVeyor.IsRunningOnAppVeyor)
	.Does(() =>
{
	foreach (var file in GetFiles(outputDir))
	{
		AppVeyor.UploadArtifact(file.FullPath);
	}
});

Task("Publish-NuGet")
	.IsDependentOn("Create-NuGet-Package")
	.WithCriteria(() => !isLocalBuild)
	.WithCriteria(() => !isPullRequest)
	.WithCriteria(() => isMainRepo)
	.WithCriteria(() => isMainBranch)
	.WithCriteria(() => isTagged)
	.Does(() =>
{
	if(string.IsNullOrEmpty(nuGetApiKey)) throw new InvalidOperationException("Could not resolve NuGet API key.");
	if(string.IsNullOrEmpty(nuGetApiUrl)) throw new InvalidOperationException("Could not resolve NuGet API url.");

	foreach(var package in GetFiles(outputDir + "*.nupkg"))
	{
		// Push the package.
		NuGetPush(package, new NuGetPushSettings {
			ApiKey = nuGetApiKey,
			Source = nuGetApiUrl
		});
	}
});

Task("Publish-GitHub-Release")
	.WithCriteria(() => !isLocalBuild)
	.WithCriteria(() => !isPullRequest)
	.WithCriteria(() => isMainRepo)
	.WithCriteria(() => isMainBranch)
	.WithCriteria(() => isTagged)
	.Does(() =>
{
	if(string.IsNullOrEmpty(gitHubUserName)) throw new InvalidOperationException("Could not resolve GitHub user name.");
	if(string.IsNullOrEmpty(gitHubPassword)) throw new InvalidOperationException("Could not resolve GitHub password.");

	GitReleaseManagerCreate(gitHubUserName, gitHubPassword, gitHubAccountName, gitHubRepo, new GitReleaseManagerCreateSettings {
		Name              = milestone,
		InputFilePath     = outputDir + "releasenotes.md",
		Prerelease        = true,
		TargetCommitish   = "master"
	});
//	GitReleaseManagerClose(gitHubUserName, gitHubPassword, gitHubAccountName, gitHubRepo, milestone);
});


///////////////////////////////////////////////////////////////////////////////
// TARGETS
///////////////////////////////////////////////////////////////////////////////

Task("Package")
	.IsDependentOn("Run-Unit-Tests")
	.IsDependentOn("Create-NuGet-Package");

Task("Coverage")
	.IsDependentOn("Generate-Code-Coverage-Report")
	.Does(() =>
{
	StartProcess("cmd", "/c start ./CodeCoverageReport/index.htm");
});

Task("AppVeyor")
	.IsDependentOn("Run-Code-Coverage")
	.IsDependentOn("Upload-Coverage-Result")
	.IsDependentOn("Create-NuGet-Package")
	.IsDependentOn("Upload-AppVeyor-Artifacts")
	.IsDependentOn("Publish-NuGet")
	.IsDependentOn("Create-Release-Notes")
	.IsDependentOn("Publish-GitHub-Release");

Task("ReleaseNotes")
	.IsDependentOn("Create-Release-Notes");

Task("Default")
	.IsDependentOn("Package");


///////////////////////////////////////////////////////////////////////////////
// EXECUTION
///////////////////////////////////////////////////////////////////////////////

RunTarget(target);
