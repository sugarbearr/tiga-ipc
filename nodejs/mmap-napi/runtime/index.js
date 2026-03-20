const binding = require('./native');
const {
  TigaServer,
  createTigaServer,
  startTigaServer,
} = require('./server');

module.exports = {
  ...binding,
  TigaServer,
  createTigaServer,
  startTigaServer,
};
