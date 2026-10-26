const fs = require('node:fs')
const path = require('node:path')
const crypto = require('node:crypto')

const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex')
const nativePath = value => value.split('/').join(path.sep)
const manifestPath = uiRoot => path.join(uiRoot, 'public', 'plm-brand-assets.json')

function writeChanged(file, bytes) {
  bytes = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes)
  if (fs.existsSync(file) && fs.readFileSync(file).equals(bytes)) return
  fs.mkdirSync(path.dirname(file), { recursive: true })
  const temporary = file + '.' + process.pid + '.tmp'
  fs.writeFileSync(temporary, bytes)
  fs.renameSync(temporary, file)
}

function verifyPlmIconAssets(uiRoot = path.resolve(__dirname, '..')) {
  const repoRoot = path.resolve(uiRoot, '../..')
  const manifest = JSON.parse(fs.readFileSync(manifestPath(uiRoot), 'utf8'))
  for (const item of [manifest.source, manifest.wordmark, manifest.generator, ...manifest.outputs]) {
    const file = path.resolve(repoRoot, nativePath(item.path))
    if (!file.startsWith(repoRoot + path.sep)) throw Error('图标素材路径超出项目目录。')
    if (!fs.existsSync(file) || sha256(fs.readFileSync(file)) !== item.sha256) {
      throw Error('图标素材不同步，请运行 pnpm --dir src/pdm-ui generate:icons：' + item.path)
    }
  }
  return manifest
}

async function generatePlmIconAssets(uiRoot = path.resolve(__dirname, '..')) {
  const sharp = require('sharp')
  const repoRoot = path.resolve(uiRoot, '../..')
  const assets = path.join(uiRoot, 'src/assets')
  const sourceFile = path.join(assets, 'plm-app-icon.svg')
  const wordmarkFile = path.join(assets, 'upton-wordmark.png')
  const source = fs.readFileSync(sourceFile)
  const wordmark = fs.readFileSync(wordmarkFile)
  const sourceHash = sha256(source)
  const svg = source.toString('utf8')
  const artwork = svg.replace(/^<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '')
  const icon = await sharp(source).resize(1024, 1024).png({ compressionLevel: 9 }).toBuffer()
  const { data, info } = await sharp(icon).ensureAlpha().raw().toBuffer({ resolveWithObject: true })
  let left = 1024, top = 1024, right = -1, bottom = -1
  for (let y = 0; y < info.height; y++) for (let x = 0; x < info.width; x++) {
    if (data[(y * info.width + x) * 4 + 3]) { left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x); bottom = Math.max(bottom, y) }
  }
  const bounds = { left, top, width: right - left + 1, height: bottom - top + 1 }
  const outputs = []
  function output(relative, bytes) {
    writeChanged(path.join(repoRoot, nativePath(relative)), bytes)
    outputs.push({ path: relative, sha256: sha256(bytes) })
  }
  async function ico(png) {
    const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    const frames = await Promise.all(sizes.map(size => sharp(png).resize(size, size).png({ compressionLevel: 9 }).toBuffer()))
    const directory = Buffer.alloc(6 + frames.length * 16)
    directory.writeUInt16LE(1, 2); directory.writeUInt16LE(frames.length, 4)
    let offset = directory.length
    frames.forEach((frame, i) => {
      const entry = 6 + i * 16, size = sizes[i]
      directory[entry] = directory[entry + 1] = size === 256 ? 0 : size
      directory.writeUInt16LE(1, entry + 4); directory.writeUInt16LE(32, entry + 6)
      directory.writeUInt32LE(frame.length, entry + 8); directory.writeUInt32LE(offset, entry + 12)
      offset += frame.length
    })
    return Buffer.concat([directory, ...frames])
  }
  output('src/pdm-ui/src/assets/pdm-client-icon.png', icon)
  output('src/pdm-ui/src/assets/plm-favicon.png', await sharp(icon).resize(64, 64).png({ compressionLevel: 9 }).toBuffer())
  const transparentIco = await ico(icon)
  output('src/pdm-ui/public/favicon.ico', transparentIco)
  output('src/Pdm.Desktop/Assets/PdmClient.ico', transparentIco)

  const desktopScale = 992 * .86 / bounds.height
  const desktopX = 512 - desktopScale * (bounds.left + bounds.width / 2)
  const desktopY = 512 - desktopScale * (bounds.top + bounds.height / 2)
  const desktopSvg = `<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024"><rect x="16" y="16" width="992" height="992" rx="218.24" fill="#FFFFFF"/><g transform="translate(${desktopX} ${desktopY}) scale(${desktopScale})">${artwork}</g></svg>`
  const desktopPng = await sharp(Buffer.from(desktopSvg)).png({ compressionLevel: 9 }).toBuffer()
  output('src/pdm-ui/src/assets/plm-desktop-icon.svg', Buffer.from(desktopSvg))
  output('src/pdm-ui/src/assets/plm-desktop-icon.png', desktopPng)
  output('src/Pdm.Desktop/Assets/UPTON-PLM.ico', await ico(desktopPng))

  output('src/pdm-ui/src/assets/upton-nav-icon.png', await sharp(icon).extract(bounds).png({ compressionLevel: 9 }).toBuffer())
  const word = await sharp(wordmark).metadata()
  const iconHeight = 141, iconWidth = iconHeight * bounds.width / bounds.height
  const gap = 16 + 3 * 143 / 38
  const wordmarkScale = .8
  const wordWidth = word.width * wordmarkScale, wordHeight = word.height * wordmarkScale
  const originalGroupWidth = iconWidth + gap + word.width
  const iconX = (481 - originalGroupWidth) / 2
  const wordX = iconX + iconWidth + gap
  const navSvg = `<svg xmlns="http://www.w3.org/2000/svg" width="481" height="143" viewBox="0 0 481 143" data-plm-source-sha256="${sourceHash}"><title>UPTON PLM</title><svg x="${iconX}" y="1" width="${iconWidth}" height="141" viewBox="${left} ${top} ${bounds.width} ${bounds.height}">${artwork}</svg><image x="${wordX}" y="${(143 - wordHeight) / 2}" width="${wordWidth}" height="${wordHeight}" href="data:image/png;base64,${wordmark.toString('base64')}"/></svg>`
  output('src/pdm-ui/src/assets/upton-nav-lockup.svg', Buffer.from(navSvg))
  output('src/pdm-ui/src/assets/upton-nav-lockup.png', await sharp(Buffer.from(navSvg)).png({ compressionLevel: 9 }).toBuffer())

  for (const name of ['index.html', 'review-overlay.html', 'server-settings.html']) {
    const file = path.join(uiRoot, name), html = fs.readFileSync(file, 'utf8')
    writeChanged(file, html.replace(/favicon\.ico\?v=[^"\s]+/g, 'favicon.ico?v=plm-' + sourceHash.slice(0, 12)))
  }
  const manifest = {
    version: 1,
    source: { path: 'src/pdm-ui/src/assets/plm-app-icon.svg', sha256: sourceHash },
    wordmark: { path: 'src/pdm-ui/src/assets/upton-wordmark.png', sha256: sha256(wordmark) },
    generator: { path: 'src/pdm-ui/tools/generate-plm-icon-assets.cjs', sha256: sha256(fs.readFileSync(__filename)) },
    bounds, desktop: { background: 'white-rounded', subjectHeightRatio: .86, cornerRadius: 218.24 },
    navigation: { gapCssPixels: gap * 38 / 143, wordmarkScale, wordmarkHeightCssPixels: wordHeight * 38 / 143, groupCentered: false },
    outputs,
  }
  writeChanged(manifestPath(uiRoot), JSON.stringify(manifest, null, 2) + '\n')
  return verifyPlmIconAssets(uiRoot)
}

module.exports = { generatePlmIconAssets, verifyPlmIconAssets }
if (require.main === module) {
  const task = process.argv.includes('--check') ? Promise.resolve().then(() => verifyPlmIconAssets()) : generatePlmIconAssets()
  task.then(manifest => console.log('图标素材已统一：' + manifest.source.sha256)).catch(error => { console.error(error.message); process.exitCode = 1 })
}
