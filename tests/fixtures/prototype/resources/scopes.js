// Resource scopes as JavaScript writes them: a try ... finally that disposes, nested per resource.
// Every function returns its log - the ids written on construction, 5 by the body, minus the ids on
// disposal - and after "|" the message of the X that left it, if any. Written for this repository.

class X extends Error {
  constructor(m) {
    super(m);
    this.name = 'X';
  }
}

// log[0] counts the entries after it.
function write(log, value) {
  log[0] = log[0] + 1;
  log[log[0]] = value;
}

// Writes its id on construction and minus its id on disposal; built with fail, Dispose throws.
class Res {
  constructor(log, id, fail) {
    this.log = log;
    this.id = id | 0;
    this.fail = fail === true;
    write(log, this.id);
  }

  Dispose() {
    write(this.log, -this.id);
    if (this.fail) throw new X('dispose ' + String.fromCharCode(48 + this.id));
  }
}

// "1,2,5,-2,-1|": the log and the message, each entry a digit.
function text(log, message) {
  let result = '';
  for (let index = 1; index <= log[0]; index++) {
    if (index > 1) result = result + ',';
    let value = log[index];
    if (value < 0) {
      result = result + '-';
      value = -value;
    }

    result = result + String.fromCharCode(48 + value);
  }

  return result + '|' + message;
}

// "1,2,5,-2,-1|": two resources, disposed in reverse order.
export function nest() {
  const log = new Int32Array(16);
  const first = new Res(log, 1, false);
  try {
    const second = new Res(log, 2, false);
    try {
      write(log, 5);
    } finally {
      if (second != null) second.Dispose();
    }
  } finally {
    if (first != null) first.Dispose();
  }

  return text(log, '');
}

// "1,5,-1|body": the body's exception leaves after disposal.
export function bodyThrows() {
  const log = new Int32Array(16);
  try {
    const resource = new Res(log, 1, false);
    try {
      write(log, 5);
      throw new X('body');
    } finally {
      if (resource != null) resource.Dispose();
    }
  } catch (e) {
    return text(log, e.message);
  }
}

// "1,5,-1|dispose 1": an exception from Dispose leaves the scope.
export function disposeThrows() {
  const log = new Int32Array(16);
  try {
    const resource = new Res(log, 1, true);
    try {
      write(log, 5);
    } finally {
      if (resource != null) resource.Dispose();
    }
  } catch (e) {
    return text(log, e.message);
  }

  return text(log, '');
}

// "5|": a null resource is not disposed.
export function nullResource() {
  const log = new Int32Array(16);
  // The log is empty: the resource is null.
  const resource = log[0] > 0 ? new Res(log, 9, false) : null;
  try {
    write(log, 5);
  } finally {
    if (resource != null) resource.Dispose();
  }

  return text(log, '');
}

// "1,2,5,-2,-1|dispose 1": both Dispose calls run; the outer one's exception replaces the inner one's.
export function bothThrow() {
  const log = new Int32Array(16);
  try {
    const outer = new Res(log, 1, true);
    try {
      const inner = new Res(log, 2, true);
      try {
        write(log, 5);
      } finally {
        if (inner != null) inner.Dispose();
      }
    } finally {
      if (outer != null) outer.Dispose();
    }
  } catch (e) {
    return text(log, e.message);
  }

  return text(log, '');
}
