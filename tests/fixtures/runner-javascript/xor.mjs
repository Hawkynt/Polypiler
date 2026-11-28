// Replaces every byte b of the buffer by b ^ 255 and returns nothing.
export function X(buf) {
  for (let index = 0; index < buf.length; ++index)
    buf[index] ^= 0xff;
}
