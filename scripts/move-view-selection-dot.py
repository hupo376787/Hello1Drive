from pathlib import Path
import re

path = Path('src/Hello1Drive.Core/Views/MainView.axaml')
text = path.read_text(encoding='utf-8')

pattern = re.compile(
    r'<MenuItem Tag="(?P<tag>Details|LargeIcons|ExtraLargeIcons)" Click="ViewContextMenu_Click">\s*'
    r'<MenuItem\.Header>\s*'
    r'<Grid ColumnDefinitions="10,\*" ColumnSpacing="6">\s*'
    r'<Ellipse Width="6" Height="6" Fill="\{DynamicResource HelloAccentBrush\}" HorizontalAlignment="Center" VerticalAlignment="Center" IsVisible="\{Binding (?P<binding>IsDetailsView|IsLargeIconView|IsExtraLargeIconView)\}" />\s*'
    r'<TextBlock Grid\.Column="1" Text="(?P<label>详细信息|大图标|超大图标)" VerticalAlignment="Center" />\s*'
    r'</Grid>\s*'
    r'</MenuItem\.Header>\s*'
    r'<MenuItem\.Icon><Path Classes="(?P<classes>[^"]+)" Data="(?P<data>[^"]+)" /></MenuItem\.Icon>',
    re.S,
)


def repl(m: re.Match) -> str:
    return f'''<MenuItem Tag="{m.group('tag')}" Click="ViewContextMenu_Click">\n                  <MenuItem.Header>\n                    <TextBlock Text="{m.group('label')}" VerticalAlignment="Center" />\n                  </MenuItem.Header>\n                  <MenuItem.Icon>\n                    <Grid ColumnDefinitions="8,14" ColumnSpacing="4" Width="26">\n                      <Ellipse Width="6" Height="6" Fill="{{DynamicResource HelloAccentBrush}}" HorizontalAlignment="Center" VerticalAlignment="Center" IsVisible="{{Binding {m.group('binding')}}}" />\n                      <Path Grid.Column="1" Classes="{m.group('classes')}" Data="{m.group('data')}" />\n                    </Grid>\n                  </MenuItem.Icon>'''

text, count = pattern.subn(repl, text)
if count < 3:
    raise RuntimeError(f'Expected at least 3 desktop view-mode items, found {count}')

path.write_text(text, encoding='utf-8', newline='\n')
print(f'Updated {count} view-mode menu items')
