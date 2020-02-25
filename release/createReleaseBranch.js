const fs = require('fs');
const cp = require('child_process');
const naturalSort = require('natural-sort');
const path = require('path');
const httpm = require('typed-rest-client/HttpClient');

const INTEGRATION_DIR = path.join(__dirname, '..', '_layout', 'integrations');
const GIT = 'git';
const VALID_RELEASE_RE = /^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$/;
const GIT_RELEASE_RE = /([0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})/;

const GIT_HUB_API_URL_ROOT="https://api.github.com/repos/microsoft/azure-pipelines-agent";

var httpc = new httpm.HttpClient('vsts-node-api');

process.env.EDITOR = process.env.EDITOR === undefined ? 'code --wait' : process.env.EDITOR;

var opt = require('node-getopt').create([
    ['',  'dryrun',               'Dry run only, do not actually commit new release'],
    ['',  'derivedFrom=version',  'Used to get PRs merged since this release was created', 'latest'],
    ['h', 'help',                 'Display this help'],
  ])
  .setHelp(
    "Usage: node mkrelease.js [OPTION] <version>\n" +
    "\n" +
    "[[OPTIONS]]\n"
  )
  .bindHelp()     // bind option 'help' to default action
  .parseSystem(); // parse command line

function verifyMinimumNodeVersion()
{
    var version = process.version;
    var minimumNodeVersion = "12.10.0"; // this is the version of node that supports the recursive option to rmdir
    if (parseFloat(version.substr(1,version.length)) < parseFloat(minimumNodeVersion))
    {
        console.log("Version of Node does not support recursive directory deletes. Be sure you are starting with a clean workspace!");

    }
    console.log("Using node version " + version);
}

function verifyMinimumGitVersion()
{
    var gitVersionOutput = cp.execSync(GIT + ' --version', { encoding: 'utf-8'});
    if (gitVersionOutput == "")
    {
        console.log("Unable to get Git Version. Got: " + gitVersionOutput);
        process.exit(-1);
    }
    var gitVersion = gitVersionOutput.match(GIT_RELEASE_RE)[0];

    var minimumGitVersion = "2.25.0"; // this is the version that supports sparse-checkout
    if (parseFloat(gitVersion) < parseFloat(minimumGitVersion))
    {
        console.log("Version of Git does not meet minimum requirement of " + minimumGitVersion);
        process.exit(-1);
    }
    console.log("Using git version " + gitVersion);

}

async function verifyNewReleaseTagOk(newRelease)
{
    if (!newRelease || !newRelease.match(VALID_RELEASE_RE) || newRelease.endsWith('.999.999'))
    {
        console.log("Invalid version '" + newRelease + "'. Version must be in the form of <major>.<minor>.<patch> where each level is 0-999");
        process.exit(-1);
    }
    var body = await (await httpc.get(GIT_HUB_API_URL_ROOT + "/releases/tags/v" + newRelease)).readBody();
    body = JSON.parse(body);
    if (body.message !== "Not Found")
    {
        console.log("Version " + newRelease + " is already in use");
        process.exit(-1);
    }
    else
    {
        console.log("Version " + newRelease + " is available for use");
    }
}

function writeAgentVersionFile(newRelease)
{
    console.log("Writing agent version file")
    if (!opt.options.dryrun)
    {
        fs.writeFileSync(path.join(__dirname, '..', 'src', 'agentversion'), newRelease  + "\n");
    }
    return newRelease;
}

async function fetchPRsSinceLastReleaseAndEditReleaseNotes(newRelease, callback)
{
    var derivedFrom = opt.options.derivedFrom;
    console.log("Derived from %o", derivedFrom);
    if (derivedFrom !== 'latest')
    {
        if (!derivedFrom.startsWith('v'))
        {
            derivedFrom = 'v' + derivedFrom;
        }
        derivedFrom = 'tags/' + derivedFrom;
    }

    var body = await (await httpc.get(GIT_HUB_API_URL_ROOT + "/releases/" + derivedFrom)).readBody();
    body = JSON.parse(body);
    if (body.published_at === undefined)
    {
        console.log('Error: Cannot find release ' + opt.options.derivedFrom + '. Aborting.');
        process.exit(-1);
    }
    var lastReleaseDate = body.published_at;
    console.log("Fetching PRs merged since " + lastReleaseDate);
    body = await (await httpc.get("https://api.github.com/search/issues?q=type:pr+is:merged+repo:microsoft/azure-pipelines-agent+merged:>=" + lastReleaseDate + "&sort=closed_at&order=asc")).readBody();
    body = JSON.parse(body);
    editReleaseNotesFile(body);
}

function editReleaseNotesFile(body)
{
    var releaseNotesFile = path.join(__dirname, '..', 'releaseNote.md');
    var existingReleaseNotes = fs.readFileSync(releaseNotesFile);
    var newPRs = { "Features": [], "Bugs": [], "Misc": [] };
    body.items.forEach(function (item) {
        var category = "Misc";
        item.labels.forEach(function (label) {
            if (category)
            {
                if (label.name === "bug")
                {
                    category = "Bugs";
                }
                if (label.name === "enhancement")
                {
                    category = "Features";
                }
                if (label.name === "internal")
                {
                    category = null;
                }
            }
        });
        if (category)
        {
            newPRs[category].push(' - ' + item.title + ' (#' + item.number + ')');
        }
    });
    var newReleaseNotes = "";
    var categories = ["Features", "Bugs", "Misc"];
    categories.forEach(function (category) {
        newReleaseNotes += "## " + category + "\n" + newPRs[category].join("\n") + "\n\n";
    });

    newReleaseNotes += existingReleaseNotes;
    var editorCmd = process.env.EDITOR + ' ' + releaseNotesFile;
    console.log(editorCmd);
    if (opt.options.dryrun)
    {
        console.log("Found the following PRs = %o", newPRs);
        console.log("\n\n");
        console.log(newReleaseNotes);
        console.log("\n");
    }
    else
    {
        fs.writeFileSync(releaseNotesFile, newReleaseNotes);
        try
        {
            cp.execSync(process.env.EDITOR + ' ' + releaseNotesFile, {
                stdio: [process.stdin, process.stdout, process.stderr]
            });
        }
        catch (err)
        {
            console.log(err.message);
            process.exit(-1);
        }
    }
}

function execInForeground(command, directory)
{
    directory = directory === undefined ? "." : directory;
    console.log("% " + command);
    if (!opt.options.dryrun)
    {
        cp.execSync(command, { cwd: directory, stdio: [process.stdin, process.stdout, process.stderr] });
    }
}

function commitAndPush(directory, release, branch)
{
    execInForeground(`${GIT} config --global user.email "azure-pipelines-bot@microsoft.com"`)
    execInForeground(`${GIT} config --global user.name "azure-pipelines-bot"`)
    execInForeground(GIT + " checkout -b " + branch, directory);
    execInForeground(`${GIT} commit -m "Agent Release ${release}" `, directory);
    execInForeground(`${GIT} -c credential.helper='!f() { echo "username=pat"; echo "password=$PAT"; };f' push --set-upstream origin ${branch}`, directory);
}

function commitAgentChanges(directory, release)
{
    var newBranch = "releases/" + release;
    execInForeground(GIT + " add " + path.join('src', 'agentversion'), directory);
    execInForeground(GIT + " add releaseNote.md", directory);
    commitAndPush(directory, release, newBranch);
}

function checkGitStatus()
{
    var git_status = cp.execSync(GIT + ' status --untracked-files=no --porcelain', { encoding: 'utf-8'});
    if (git_status !== "")
    {
        console.log("You have uncommited changes in this clone. Aborting.");
        console.log(git_status);
        if (!opt.options.dryrun)
        {
            process.exit(-1);
        }
    }
    else
    {
        console.log("Git repo is clean.");
    }
    return git_status;
}

async function main()
{
    var newRelease = opt.argv[0];
    if (newRelease === undefined)
    {
        console.log('Error: You must supply a version');
        process.exit(-1);
    }
    verifyMinimumNodeVersion();
    verifyMinimumGitVersion();
    await verifyNewReleaseTagOk(newRelease);
    checkGitStatus();
    writeAgentVersionFile(newRelease);
    await fetchPRsSinceLastReleaseAndEditReleaseNotes(newRelease);
    commitAgentChanges(path.join(__dirname, '..'), newRelease);
    console.log('done.');
}

main();