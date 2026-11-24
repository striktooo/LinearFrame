# Draws docs/images/first-run.png and first-run-pl.png: the two clicks that get a new user past the blue
# "Windows protected your PC" window (shown once for every program that is not code-signed yet).
# A drawing, not a screenshot - nothing from this PC is in it.   pwsh scripts/first-run-picture.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$xaml = @'
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="1120" Height="330"
      TextElement.FontFamily="Segoe UI Variable Display, Segoe UI" UseLayoutRounding="True" SnapsToDevicePixels="True">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="*"/><ColumnDefinition Width="64"/><ColumnDefinition Width="*"/>
  </Grid.ColumnDefinitions>

  <Border Grid.Column="0" Background="#0067C0" CornerRadius="10" Padding="28,22,22,20">
    <Grid>
      <StackPanel>
        <TextBlock Text="{TITLE}" Foreground="White" FontSize="25" FontWeight="Light"/>
        <Rectangle Height="9" RadiusX="4.5" RadiusY="4.5" Fill="#55FFFFFF" Margin="0,24,46,0"/>
        <Rectangle Height="9" RadiusX="4.5" RadiusY="4.5" Fill="#55FFFFFF" Margin="0,11,170,0"/>
        <StackPanel Orientation="Horizontal" Margin="-12,22,0,0">
          <Border BorderBrush="#FFD60A" BorderThickness="3" CornerRadius="9" Padding="10,5,10,7">
            <TextBlock Text="{MORE}" Foreground="White" FontSize="17" TextDecorations="Underline"/>
          </Border>
          <Border Width="34" Height="34" CornerRadius="17" Background="#FFD60A" Margin="12,0,0,0" VerticalAlignment="Center">
            <TextBlock Text="1" Foreground="#1B1B1F" FontSize="19" FontWeight="Bold" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="0,-2,0,0"/>
          </Border>
        </StackPanel>
      </StackPanel>
      <Border VerticalAlignment="Bottom" HorizontalAlignment="Right" Background="#E9E9E9" CornerRadius="3" Padding="22,7,22,8" Opacity="0.55">
        <TextBlock Text="{DONT}" Foreground="#1B1B1F" FontSize="14.5"/>
      </Border>
    </Grid>
  </Border>

  <Path Grid.Column="1" Stroke="#8B949E" StrokeThickness="4" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
        Data="M 16,165 L 46,165 M 35,153 L 47,165 L 35,177" />

  <Border Grid.Column="2" Background="#0067C0" CornerRadius="10" Padding="28,22,22,20">
    <Grid>
      <StackPanel>
        <TextBlock Text="{TITLE}" Foreground="White" FontSize="25" FontWeight="Light"/>
        <Rectangle Height="9" RadiusX="4.5" RadiusY="4.5" Fill="#55FFFFFF" Margin="0,24,46,0"/>
        <Rectangle Height="9" RadiusX="4.5" RadiusY="4.5" Fill="#55FFFFFF" Margin="0,11,170,0"/>
        <Grid Margin="0,22,0,0">
          <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
          <Grid.RowDefinitions><RowDefinition/><RowDefinition/></Grid.RowDefinitions>
          <TextBlock Text="{APP}" Foreground="#D9FFFFFF" FontSize="14.5" Margin="0,0,14,5"/>
          <TextBlock Grid.Column="1" Text="LinearFrame.exe" Foreground="White" FontSize="14.5"/>
          <TextBlock Grid.Row="1" Text="{PUBLISHER}" Foreground="#D9FFFFFF" FontSize="14.5" Margin="0,0,14,0"/>
          <TextBlock Grid.Row="1" Grid.Column="1" Text="{UNKNOWN}" Foreground="White" FontSize="14.5"/>
        </Grid>
      </StackPanel>
      <StackPanel Orientation="Horizontal" VerticalAlignment="Bottom" HorizontalAlignment="Right">
        <Border Width="34" Height="34" CornerRadius="17" Background="#FFD60A" Margin="0,0,12,0" VerticalAlignment="Center">
          <TextBlock Text="2" Foreground="#1B1B1F" FontSize="19" FontWeight="Bold" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="0,-2,0,0"/>
        </Border>
        <Border BorderBrush="#FFD60A" BorderThickness="3" CornerRadius="8" Padding="4" Margin="0,-7,10,-7">
          <Border Background="#E9E9E9" CornerRadius="3" Padding="22,7,22,8">
            <TextBlock Text="{RUN}" Foreground="#1B1B1F" FontSize="14.5" FontWeight="SemiBold"/>
          </Border>
        </Border>
        <Border Background="#E9E9E9" CornerRadius="3" Padding="22,7,22,8" Opacity="0.55" VerticalAlignment="Center">
          <TextBlock Text="{DONT}" Foreground="#1B1B1F" FontSize="14.5"/>
        </Border>
      </StackPanel>
    </Grid>
  </Border>
</Grid>
'@

$texts = @{
    'first-run.png'    = @{ TITLE = 'Windows protected your PC'; MORE = 'More info'; DONT = "Don't run"; RUN = 'Run anyway'
                            APP = 'App:'; PUBLISHER = 'Publisher:'; UNKNOWN = 'Unknown publisher' }
    'first-run-pl.png' = @{ TITLE = 'System Windows ochronił ten komputer'; MORE = 'Więcej informacji'; DONT = 'Nie uruchamiaj'; RUN = 'Uruchom mimo to'
                            APP = 'Aplikacja:'; PUBLISHER = 'Wydawca:'; UNKNOWN = 'Nieznany wydawca' }
}
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\images'
foreach ($name in $texts.Keys) {
    $x = $xaml
    foreach ($k in $texts[$name].Keys) { $x = $x.Replace("{$k}", [Security.SecurityElement]::Escape($texts[$name][$k])) }
    $root = [Windows.Markup.XamlReader]::Parse($x)
    $size = [Windows.Size]::new($root.Width, $root.Height)
    $root.Measure($size); $root.Arrange([Windows.Rect]::new($size)); $root.UpdateLayout()
    $bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]($root.Width * 2), [int]($root.Height * 2), 192, 192, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($root)
    $enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $file = Join-Path $out $name
    $fs = [IO.File]::Create($file); try { $enc.Save($fs) } finally { $fs.Dispose() }
    "{0}: {1}x{2}, {3:N0} bytes" -f $name, $bmp.PixelWidth, $bmp.PixelHeight, (Get-Item $file).Length
}
