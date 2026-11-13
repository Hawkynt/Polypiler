// A run-length decoder written as a state machine over a switch. A control byte below 0x80 starts a
// literal run of control + 1 bytes, a control byte from 0x80 to 0xFE repeats the byte that follows it
// control - 0x7D times, and 0xFF ends the stream. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RunLength {

  public static int Decode(byte[] input, byte[] output) {
    const int Control = 0;
    const int Literal = 1;
    const int Repeat = 2;
    const int Done = 3;

    int state = Control;
    int count = 0;
    int written = 0;
    for (int i = 0; i < input.Length; ++i) {
      byte value = input[i];
      switch (state) {
        case Control:
          if (value == 0xFF) {
            state = Done;
          } else if (value < 0x80) {
            state = Literal;
            count = value + 1;
          } else {
            state = Repeat;
            count = value - 0x7D;
          }

          break;
        case Literal:
          output[written++] = value;
          if (--count == 0)
            state = Control;

          break;
        case Repeat:
          while (count > 0) {
            output[written++] = value;
            --count;
          }

          state = Control;
          break;
        default:
          return written;
      }
    }

    return written;
  }

  // The number of bytes Decode writes, found without writing them.
  public static int DecodedLength(byte[] input) {
    int length = 0;
    int i = 0;
    while (i < input.Length) {
      byte control = input[i++];
      switch (control >> 7) {
        case 0:
          length += control + 1;
          i += control + 1;
          continue;
        case 1:
          if (control == 0xFF)
            return length;

          length += control - 0x7D;
          ++i;
          break;
      }
    }

    return length;
  }

}
