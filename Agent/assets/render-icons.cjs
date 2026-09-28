const fs = require('node:fs');
const path = require('node:path');
const { Resvg } = require('@resvg/resvg-js');
const directory = path.join(__dirname, 'icons');
for (const name of fs.readdirSync(directory).filter(name => name.endsWith('.svg'))) {
  const source = fs.readFileSync(path.join(directory, name), 'utf8');
  const png = new Resvg(source, { fitTo: { mode: 'width', value: 48 } }).render().asPng();
  fs.writeFileSync(path.join(directory, name.replace(/\.svg$/, '.png')), png);
  console.log(name);
}
