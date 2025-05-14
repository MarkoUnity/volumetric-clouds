# Volumetric Clouds
This project is based of https://github.com/yangrc1234/VolumeCloud#
A high-quality volumetric cloud rendering system for Unity that provides realistic cloud rendering with customizable parameters.
The cloud rendering logic has been updated to support adaptive raymarch sampling count based on distance and viewing angle.
This is a minimum set of logic required for a working cloud rendering system.
For more details and features please visit https://github.com/yangrc1234/VolumeCloud#


## Features

- Realistic volumetric cloud rendering
- Customizable cloud parameters
- Height-based cloud generation
- Efficient raymarching implementation
- Example scene included

## Installation

### Using Unity Package Manager

1. Open your Unity project
2. Go to Window > Package Manager
3. Click the + button in the top-left corner
4. Select "Add package from git URL..."
5. Enter the following URL:
   ```
   https://github.com/yourusername/VolumetricClouds.git
   ```
6. Click "Add"

### Manual Installation

1. Download the latest release from the [Releases page](https://github.com/yourusername/VolumetricClouds/releases)
2. Extract the package into your Unity project's `Packages` folder
3. The package will be automatically recognized by Unity

## Usage

1. Import the package into your project
2. Open the example scene in `Example/Scenes/Example.unity`
3. Add the `VolumetricCloudRenderer` component to your camera
4. Configure the cloud parameters using the `VolumetricCloudConfiguration` component

## Requirements

- Unity 2022.3 or later
- Built-in Render Pipeline

## License

This project is licensed under the terms specified in the [LICENSE](License.txt) file.
