// Assigns a String to an int: javac rejects it with "incompatible types".
public final class TypeError {

  public static void main(String[] args) {
    int count = "text";
    System.out.println(count);
  }

}
