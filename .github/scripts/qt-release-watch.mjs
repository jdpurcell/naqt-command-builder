const folderWatchVariable = 'QT_UPDATE_FOLDER_WATCH';
const releaseNotesWatchVariable = 'QT_RELEASE_NOTES_WATCH';

const [owner, repo] = (process.env.GITHUB_REPOSITORY || '').split('/');
const issueToken = process.env.GITHUB_TOKEN;
const variableToken = process.env.QT_RELEASE_WATCH_TOKEN; // Fine-grained PAT w/ Variables: Write
const githubApiUrl = process.env.GITHUB_API_URL || 'https://api.github.com';

if (!owner || !repo) {
  throw new Error('GITHUB_REPOSITORY is not set.');
}

if (!issueToken) {
  throw new Error('GITHUB_TOKEN is not set.');
}

if (!variableToken) {
  throw new Error('QT_RELEASE_WATCH_TOKEN is not set.');
}

function parseVersionList(value) {
  if (!value) {
    return [];
  }

  return [...new Set(
    value
      .split(';')
      .map((entry) => entry.trim())
      .filter(Boolean),
  )];
}

function buildFolderUrl(version) {
  const majorVersion = version.split('.')[0];
  const versionNoDots = version.replaceAll('.', '');
  return `https://download.qt.io/online/qtsdkrepository/linux_x64/desktop/qt${majorVersion}_${versionNoDots}/`;
}

function buildReleaseNotesUrl(version) {
  return `https://code.qt.io/cgit/qt/qtreleasenotes.git/about/qt/${version}/release-note.md`;
}

function extractMarkdownBodyInnerHtml(html) {
  const match = html.match(/<div\s+[^>]*class=(['"])[^'"]*\bmarkdown-body\b[^'"]*\1[^>]*>([\s\S]*?)<\/div>/i);
  if (!match) {
    return '';
  }

  return match[2]
    .replace(/<!--([\s\S]*?)-->/g, '')
    .trim();
}

async function githubRequest(method, path, body, options = {}) {
  const response = await fetch(`${githubApiUrl}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${options.token}`,
      Accept: 'application/vnd.github+json',
      'Content-Type': 'application/json',
      'User-Agent': 'qt-release-watch-workflow',
      'X-GitHub-Api-Version': '2022-11-28',
    },
    body: body ? JSON.stringify(body) : undefined,
  });

  if (options.allow404 && response.status === 404) {
    return null;
  }

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`GitHub API ${method} ${path} failed with ${response.status}: ${text}`);
  }

  if (response.status === 204) {
    return null;
  }

  return response.json();
}

async function updateRepositoryVariable(name, value) {
  await githubRequest(
    'PATCH',
    `/repos/${owner}/${repo}/actions/variables/${name}`,
    {
      name,
      value: value || ';',
    },
    { token: variableToken },
  );
}

async function createIssue(title, body) {
  return githubRequest(
    'POST',
    `/repos/${owner}/${repo}/issues`,
    {
      title,
      body,
    },
    { token: issueToken },
  );
}

async function checkFolderRelease(version) {
  const url = buildFolderUrl(version);
  const response = await fetch(url, {
    method: 'GET',
    redirect: 'follow',
    headers: {
      'User-Agent': 'qt-release-watch-workflow',
    },
  });

  return {
    exists: response.ok,
    url,
  };
}

async function checkReleaseNotes(version) {
  const url = buildReleaseNotesUrl(version);
  const response = await fetch(url, {
    method: 'GET',
    redirect: 'follow',
    headers: {
      'User-Agent': 'qt-release-watch-workflow',
    },
  });

  if (!response.ok) {
    return {
      exists: false,
      url,
    };
  }

  const html = await response.text();
  const innerHtml = extractMarkdownBodyInnerHtml(html);

  return {
    exists: innerHtml.length > 0,
    url,
  };
}

async function notifyForMatches({
  watchType,
  variableName,
  versions,
  checkRelease,
}) {
  if (versions.length === 0) {
    console.log(`${variableName} is empty; skipping ${watchType} checks.`);
    return [];
  }

  console.log(`Checking ${watchType} for ${versions.length} version(s) from ${variableName}.`);

  const results = await Promise.all(
    versions.map(async (version) => ({
      version,
      ...(await checkRelease(version)),
    })),
  );

  const matchedResults = results.filter((result) => {
    if (!result.exists) {
      console.log(`No ${watchType} release signal for ${result.version}.`);
      return false;
    }

    return true;
  });

  if (matchedResults.length === 0) {
    return [];
  }

  const matchedVersions = matchedResults.map((result) => result.version);
  const remainingVersions = versions.filter((version) => !matchedVersions.includes(version));
  await updateRepositoryVariable(variableName, remainingVersions.join(';'));
  console.log(
    `Updated ${variableName}; removed ${matchedVersions.length} matched version(s), ${remainingVersions.length} remain(s) queued.`,
  );

  for (const result of matchedResults) {
    const title = `Detected new Qt ${watchType} for ${result.version}`;
    const body = [
      `Source URL: ${result.url}`,
      `Detected at: ${new Date().toISOString()}`,
    ].join('\n');

    const issue = await createIssue(title, body);
    console.log(`Created issue #${issue.number} for ${watchType} ${result.version}.`);
  }

  return matchedVersions;
}

async function main() {
  const folderVersions = parseVersionList(process.env[folderWatchVariable]);
  const releaseNotesVersions = parseVersionList(process.env[releaseNotesWatchVariable]);

  if (folderVersions.length === 0) {
    console.log(`${folderWatchVariable} is empty or not set; skipping folder watch.`);
  }

  if (releaseNotesVersions.length === 0) {
    console.log(`${releaseNotesWatchVariable} is empty or not set; skipping release notes watch.`);
  }

  const [folderMatches, releaseNotesMatches] = await Promise.all([
    notifyForMatches({
      watchType: 'update folder',
      variableName: folderWatchVariable,
      versions: folderVersions,
      checkRelease: checkFolderRelease,
    }),
    notifyForMatches({
      watchType: 'release notes',
      variableName: releaseNotesWatchVariable,
      versions: releaseNotesVersions,
      checkRelease: checkReleaseNotes,
    }),
  ]);

  console.log(
    `Finished. Folder matches: ${folderMatches.length}. Release notes matches: ${releaseNotesMatches.length}.`,
  );
}

await main();
