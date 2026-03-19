const binding = require('./index.node');

module.exports = {
  initialized: binding.initialized,
  tigaWrite: binding.tigaWrite,
  tigaRead: binding.tigaRead,
  tigaInvoke: binding.tigaInvoke,
};
