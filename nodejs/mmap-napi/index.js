const mmapNeon = require('./index.node');

module.exports = {
  initialized: mmapNeon.initialized,
  write: mmapNeon.write,
  read: mmapNeon.read,
  tigaWrite: mmapNeon.tigaWrite,
  tigaRead: mmapNeon.tigaRead,
  tigaInvoke: mmapNeon.tigaInvoke,
};
