// An unchecked cast: only a warning, which -Xlint:all -Werror turns into a failed build.
import java.util.List;

public final class Warn {

  public static void main(String[] args) {
    Object items = List.of(args);
    List<String> names = (List<String>) items;
    System.out.println(names.size());
  }

}
