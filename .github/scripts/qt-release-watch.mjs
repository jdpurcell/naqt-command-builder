const folderWatchVariable = 'QT_UPDATE_FOLDER_WATCH';
const releaseNotesWatchVariable = 'QT_RELEASE_NOTES_WATCH';
const extensionWatchVariable = 'QT_EXTENSION_FOLDER_WATCH';

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

function compareVersions(left, right) {
  const a = left.split('.').map(Number);
  const b = right.split('.').map(Number);
  return a[0] - b[0] || a[1] - b[1] || a[2] - b[2];
}

function parseIndexDirectories(html) {
  const directories = [];
  const textContent = (value) => value.replace(/<[^>]*>/g, '').replace(/&nbsp;|&#160;/gi, ' ').trim();
  for (const row of html.matchAll(/<tr\b[^>]*>([\s\S]*?)<\/tr>/gi)) {
    const cells = [...row[1].matchAll(/<td\b[^>]*>([\s\S]*?)<\/td>/gi)];
    if (cells.length < 3 || textContent(cells.at(-1)[1]) !== '') {
      continue;
    }
    const name = textContent(cells[1][1]);
    if (name.toLowerCase() !== 'parent directory') {
      directories.push(name.replace(/\/$/, ''));
    }
  }
  return directories;
}

async function notifyForExtensionMatches() {
  const entries = parseVersionList(process.env[extensionWatchVariable]);
  // Validate every entry before making requests or changing the variable.
  const watches = entries.map((entry) => {
    const match = entry.match(/^(\d+\.\d+\.\d+):([a-zA-Z0-9_-]+)>(\d+\.\d+\.\d+)$/);
    if (!match) {
      throw new Error(`Invalid ${extensionWatchVariable} entry: ${entry}. Expected QtVersion:extension>extensionVersion.`);
    }
    return { qtVersion: match[1], extension: match[2], minimumVersion: match[3] };
  });

  if (watches.length === 0) {
    console.log(`${extensionWatchVariable} is empty; skipping extension checks.`);
    return [];
  }

  const matches = [];
  for (const [index, watch] of watches.entries()) {
    const { qtVersion, extension, minimumVersion } = watch;
    const majorVersion = qtVersion.split('.')[0];
    const url = `https://download.qt.io/online/qtsdkrepository/linux_x64/extensions/${extension}/${qtVersion.replaceAll('.', '')}/`;
    const response = await fetch(url, {
      headers: { 'User-Agent': 'qt-release-watch-workflow' },
      redirect: 'follow',
    });
    if (response.status === 404) {
      console.log(`No extension folder for ${extension} / Qt ${qtVersion}.`);
      continue;
    }
    if (!response.ok) {
      throw new Error(`Extension listing ${url} failed with ${response.status}.`);
    }

    const directoryPattern = new RegExp(`^${majorVersion}(\\d{3})(\\d)$`);
    const versions = [...new Set(parseIndexDirectories(await response.text())
      .map((name) => name.match(directoryPattern))
      .filter(Boolean)
      .map((match) => `${majorVersion}.${Number(match[1])}.${Number(match[2])}`))]
      .filter((version) => compareVersions(version, minimumVersion) > 0)
      .sort(compareVersions);

    for (const version of versions) {
      const sourceUrl = `${url}${majorVersion}${version.split('.')[1].padStart(3, '0')}${version.split('.')[2]}/`;
      const issue = await createIssue(
        `Detected new Qt extension ${extension} ${version} for Qt ${qtVersion}`,
        [`Source URL: ${sourceUrl}`, `Detected at: ${new Date().toISOString()}`].join('\n'),
      );
      console.log(`Created issue #${issue.number} for ${extension} ${version} / Qt ${qtVersion}.`);
      matches.push({ qtVersion, extension, version });
    }

    if (versions.length > 0) {
      entries[index] = `${qtVersion}:${extension}>${versions.at(-1)}`;
      await updateRepositoryVariable(extensionWatchVariable, entries.join(';'));
      console.log(`Updated ${extensionWatchVariable}: ${entries[index]}.`);
    }
  }
  return matches;
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

  const [folderMatches, releaseNotesMatches, extensionMatches] = await Promise.all([
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
    notifyForExtensionMatches(),
  ]);

  console.log(
    `Finished. Folder matches: ${folderMatches.length}. Release notes matches: ${releaseNotesMatches.length}. Extension matches: ${extensionMatches.length}.`,
  );
}

await main();
